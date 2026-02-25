// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.Threading.Analyzers;

public class CommonInterestParsingTests
{
    public class TryParseNegatableTypeOrMemberReferenceTests
    {
        [Fact]
        public void TypeOnly()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[MyType]", out bool negated, out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.False(negated);
            Assert.Equal("MyType", typeName.ToString());
            Assert.Null(memberName);
        }

        [Fact]
        public void TypeWithNamespace()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[My.Namespace.MyType]", out bool negated, out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.False(negated);
            Assert.Equal("My.Namespace.MyType", typeName.ToString());
            Assert.Null(memberName);
        }

        [Fact]
        public void TypeAndMember()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[MyType]::MyMethod", out bool negated, out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.False(negated);
            Assert.Equal("MyType", typeName.ToString());
            Assert.Equal("MyMethod", memberName);
        }

        [Fact]
        public void TypeWithNamespaceAndMember()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[My.Namespace.MyType]::MyMethod", out bool negated, out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.False(negated);
            Assert.Equal("My.Namespace.MyType", typeName.ToString());
            Assert.Equal("MyMethod", memberName);
        }

        [Fact]
        public void NegatedTypeOnly()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("![MyType]", out bool negated, out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.True(negated);
            Assert.Equal("MyType", typeName.ToString());
            Assert.Null(memberName);
        }

        [Fact]
        public void NegatedTypeAndMember()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("![MyType]::MyMethod", out bool negated, out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.True(negated);
            Assert.Equal("MyType", typeName.ToString());
            Assert.Equal("MyMethod", memberName);
        }

        [Fact]
        public void TrailingWhitespace()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[MyType]  ", out _, out ReadOnlyMemory<char> typeName, out _));
            Assert.Equal("MyType", typeName.ToString());
        }

        [Fact]
        public void TrailingWhitespaceAfterMember()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[MyType]::MyMethod  ", out _, out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.Equal("MyType", typeName.ToString());
            Assert.Equal("MyMethod", memberName);
        }

        /// <summary>
        /// Regression test: the long type name from the bug report must parse quickly without catastrophic backtracking.
        /// </summary>
        [Fact]
        public void LongQualifiedTypeNameFromBugReport()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("![Microsoft.VisualStudio.Shell.Interop.IVsRunningDocumentTablePrivate]", out bool negated, out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.True(negated);
            Assert.Equal("Microsoft.VisualStudio.Shell.Interop.IVsRunningDocumentTablePrivate", typeName.ToString());
            Assert.Null(memberName);
        }

        [Fact]
        public void EmptyString()
        {
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference(string.Empty, out _, out _, out _));
        }

        [Fact]
        public void NoBrackets()
        {
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("MyType", out _, out _, out _));
        }

        [Fact]
        public void MissingOpeningBracket()
        {
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("MyType]", out _, out _, out _));
        }

        [Fact]
        public void MissingClosingBracket()
        {
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[MyType", out _, out _, out _));
        }

        [Fact]
        public void EmptyTypeName()
        {
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[]", out _, out _, out _));
        }

        [Fact]
        public void SingleColonNotDouble()
        {
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[MyType]:MyMethod", out _, out _, out _));
        }

        [Fact]
        public void DoubleColonWithoutMemberName()
        {
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[MyType]::", out _, out _, out _));
        }

        [Fact]
        public void ColonInsideBrackets()
        {
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[MyType::NotAllowed]", out _, out _, out _));
        }

        [Fact]
        public void LeadingWhitespace()
        {
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("  [MyType]", out _, out _, out _));
        }

        [Fact]
        public void SpaceInMiddleOfMemberName()
        {
            // The member name scanner stops at whitespace, so "extra" content after a space is not consumed
            // and the trailing-only-whitespace check rejects the line.
            Assert.False(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[MyType]::MyMethod extra", out _, out _, out _));
        }

        [Fact]
        public void WildcardTypeName()
        {
            Assert.True(CommonInterestParsing.TryParseNegatableTypeOrMemberReference("[*]", out bool negated, out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.False(negated);
            Assert.Equal("*", typeName.ToString());
            Assert.Null(memberName);
        }
    }

    public class TryParseMemberReferenceTests
    {
        [Fact]
        public void TypeAndMember()
        {
            Assert.True(CommonInterestParsing.TryParseMemberReference("[MyType]::MyMethod", out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.Equal("MyType", typeName.ToString());
            Assert.Equal("MyMethod", memberName);
        }

        [Fact]
        public void TypeWithNamespaceAndMember()
        {
            Assert.True(CommonInterestParsing.TryParseMemberReference("[My.Namespace.MyType]::MyMethod", out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.Equal("My.Namespace.MyType", typeName.ToString());
            Assert.Equal("MyMethod", memberName);
        }

        [Fact]
        public void TrailingWhitespace()
        {
            Assert.True(CommonInterestParsing.TryParseMemberReference("[MyType]::MyMethod  ", out ReadOnlyMemory<char> typeName, out string? memberName));
            Assert.Equal("MyType", typeName.ToString());
            Assert.Equal("MyMethod", memberName);
        }

        [Fact]
        public void EmptyString()
        {
            Assert.False(CommonInterestParsing.TryParseMemberReference(string.Empty, out _, out _));
        }

        [Fact]
        public void TypeOnly_NoMember()
        {
            Assert.False(CommonInterestParsing.TryParseMemberReference("[MyType]", out _, out _));
        }

        [Fact]
        public void NoBrackets()
        {
            Assert.False(CommonInterestParsing.TryParseMemberReference("MyType::MyMethod", out _, out _));
        }

        [Fact]
        public void MissingClosingBracket()
        {
            Assert.False(CommonInterestParsing.TryParseMemberReference("[MyType::MyMethod", out _, out _));
        }

        [Fact]
        public void EmptyTypeName()
        {
            Assert.False(CommonInterestParsing.TryParseMemberReference("[]::MyMethod", out _, out _));
        }

        [Fact]
        public void SingleColonNotDouble()
        {
            Assert.False(CommonInterestParsing.TryParseMemberReference("[MyType]:MyMethod", out _, out _));
        }

        [Fact]
        public void DoubleColonWithoutMemberName()
        {
            Assert.False(CommonInterestParsing.TryParseMemberReference("[MyType]::", out _, out _));
        }

        [Fact]
        public void LeadingWhitespace()
        {
            Assert.False(CommonInterestParsing.TryParseMemberReference("  [MyType]::MyMethod", out _, out _));
        }

        [Fact]
        public void Negation_NotSupported()
        {
            // TryParseMemberReference does not accept a leading '!'.
            Assert.False(CommonInterestParsing.TryParseMemberReference("![MyType]::MyMethod", out _, out _));
        }
    }
}
