// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Parsing helpers for additional-file lines used by <see cref="CommonInterest" />.
/// This class is intentionally kept free of Roslyn dependencies so it can be
/// linked directly into test projects for unit testing.
/// </summary>
internal static class CommonInterestParsing
{
    /// <summary>
    /// Parses a line that may begin with an optional <c>!</c>, followed by <c>[TypeName]</c>,
    /// and optionally <c>::MemberName</c>, without using regular expressions.
    /// </summary>
    /// <param name="line">The line to parse.</param>
    /// <param name="negated"><see langword="true" /> if the line begins with '!'.</param>
    /// <param name="typeName">The type name parsed from the brackets.</param>
    /// <param name="memberName">The member name after '::', or <see langword="null" /> if not present.</param>
    /// <returns><see langword="true" /> if parsing succeeded.</returns>
    internal static bool TryParseNegatableTypeOrMemberReference(string line, out bool negated, out ReadOnlyMemory<char> typeName, out string? memberName)
    {
        negated = false;
        typeName = default;
        memberName = null;

        ReadOnlySpan<char> span = line.AsSpan();
        int pos = 0;

        // Optional negation prefix.
        if (pos < span.Length && span[pos] == '!')
        {
            negated = true;
            pos++;
        }

        // Required '[TypeName]'.
        int bracketStart = pos;
        if (!TryParseBracketedTypeName(span, ref pos, out _))
        {
            return false;
        }

        // Compute memory slice for type name (between the brackets), using recorded bracket position.
        ReadOnlyMemory<char> typeNameMemory = line.AsMemory(bracketStart + 1, pos - bracketStart - 2);

        // Optional '::memberName'.
        ReadOnlySpan<char> memberNameSpan = default;
        if (pos + 1 < span.Length && span[pos] == ':' && span[pos + 1] == ':')
        {
            pos += 2;
            int memberNameStart = pos;
            while (pos < span.Length && !char.IsWhiteSpace(span[pos]))
            {
                pos++;
            }

            if (pos == memberNameStart)
            {
                // '::' present but no member name follows.
                return false;
            }

            memberNameSpan = span.Slice(memberNameStart, pos - memberNameStart);
        }

        // Allow only trailing whitespace.
        while (pos < span.Length && char.IsWhiteSpace(span[pos]))
        {
            pos++;
        }

        if (pos != span.Length)
        {
            return false;
        }

        // Only allocate strings after full validation.
        typeName = typeNameMemory;
        memberName = memberNameSpan.IsEmpty ? null : memberNameSpan.ToString();
        return true;
    }

    /// <summary>
    /// Parses a line of the form <c>[TypeName]::MemberName</c>, without using regular expressions.
    /// </summary>
    /// <param name="line">The line to parse.</param>
    /// <param name="typeName">The type name parsed from the brackets.</param>
    /// <param name="memberName">The member name after '::'.</param>
    /// <returns><see langword="true" /> if parsing succeeded.</returns>
    internal static bool TryParseMemberReference(string line, out ReadOnlyMemory<char> typeName, out string? memberName)
    {
        typeName = default;
        memberName = null;

        ReadOnlySpan<char> span = line.AsSpan();
        int pos = 0;

        // Required '[TypeName]'.
        int bracketStart = pos;
        if (!TryParseBracketedTypeName(span, ref pos, out _))
        {
            return false;
        }

        // Compute memory slice for type name (between the brackets), using recorded bracket position.
        ReadOnlyMemory<char> typeNameMemory = line.AsMemory(bracketStart + 1, pos - bracketStart - 2);

        // Required '::'.
        if (pos + 1 >= span.Length || span[pos] != ':' || span[pos + 1] != ':')
        {
            return false;
        }

        pos += 2;

        // Member name: one or more non-whitespace chars.
        int memberNameStart = pos;
        while (pos < span.Length && !char.IsWhiteSpace(span[pos]))
        {
            pos++;
        }

        if (pos == memberNameStart)
        {
            return false;
        }

        ReadOnlySpan<char> memberNameSpan = span.Slice(memberNameStart, pos - memberNameStart);

        // Allow only trailing whitespace.
        while (pos < span.Length && char.IsWhiteSpace(span[pos]))
        {
            pos++;
        }

        if (pos != span.Length)
        {
            return false;
        }

        // Only allocate strings after full validation.
        typeName = typeNameMemory;
        memberName = memberNameSpan.ToString();
        return true;
    }

    /// <summary>
    /// Advances <paramref name="pos" /> past a <c>[TypeName]</c> token and outputs the type-name span.
    /// </summary>
    /// <param name="span">The full input span.</param>
    /// <param name="pos">The current parse position; advanced past the closing <c>]</c> on success.</param>
    /// <param name="typeName">A slice of <paramref name="span" /> containing the type name, without the brackets.</param>
    /// <returns><see langword="true" /> if a non-empty bracketed type name was consumed.</returns>
    internal static bool TryParseBracketedTypeName(ReadOnlySpan<char> span, ref int pos, out ReadOnlySpan<char> typeName)
    {
        typeName = default;

        // Required opening bracket.
        if (pos >= span.Length || span[pos] != '[')
        {
            return false;
        }

        pos++;

        // Type name: one or more chars that are not '[', ']', or ':'.
        int typeNameStart = pos;
        while (pos < span.Length && span[pos] != '[' && span[pos] != ']' && span[pos] != ':')
        {
            pos++;
        }

        if (pos == typeNameStart)
        {
            return false;
        }

        typeName = span.Slice(typeNameStart, pos - typeNameStart);

        // Required closing bracket.
        if (pos >= span.Length || span[pos] != ']')
        {
            return false;
        }

        pos++;
        return true;
    }
}
