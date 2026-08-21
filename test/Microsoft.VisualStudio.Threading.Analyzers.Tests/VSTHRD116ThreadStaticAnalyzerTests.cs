// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD116ThreadStaticAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;
using VBVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.VisualBasicCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VSTHRD116ThreadStaticAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class VSTHRD116ThreadStaticAnalyzerTests
{
    [Fact]
    public async Task ValidUses_CSharp()
    {
        const string test = """
            using System;
            using ThreadLocal = System.ThreadStaticAttribute;

            class Test
            {
                [ThreadStatic]
                private static object field;

                [ThreadLocal]
                private static int aliasedField;

                [field: ThreadStatic]
                private static object Property { get; set; }

                static Test()
                {
                    field = new object();
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task NonStaticFields_CSharp()
    {
        const string test = """
            using System;

            class Test
            {
                [ThreadStatic]
                private object {|VSTHRD116:first|};

                [global::System.ThreadStaticAttribute]
                private int {|VSTHRD116:second|}, {|VSTHRD116:third|};

                [field: ThreadStatic]
                private object {|VSTHRD116:Property|} { get; set; } = new object();

            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AutoPropertyInlineInitialization_CSharp()
    {
        const string test = """
            using System;

            class Test
            {
                [field: ThreadStatic]
                private static object Property { get; set; } {|VSTHRD117:= new object()|};
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Theory]
    [InlineData("object", "new object()")]
    [InlineData("object", "default")]
    [InlineData("object", "null")]
    [InlineData("int", "42")]
    [InlineData("int", "default")]
    [InlineData("int", "0")]
    public async Task InlineInitialization_CSharp(string type, string value)
    {
        string test = $$"""
            using System;

            class Test
            {
                [ThreadStatic]
                private static {{type}} field {|VSTHRD117:= {{value}}|};
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task NonStaticInitializedFieldOnlyReportsNonStaticDiagnostic_CSharp()
    {
        const string test = """
            using System;

            class Test
            {
                [ThreadStatic]
                private object {|VSTHRD116:field|} = new object();
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task UnrelatedAttributeAndUnattributedFields_CSharp()
    {
        const string test = """
            namespace Custom
            {
                class ThreadStaticAttribute : System.Attribute
                {
                }
            }

            class Test
            {
                [Custom.ThreadStatic]
                private object instanceField = new object();

                private static object staticField = new object();
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task ValidUses_VisualBasic()
    {
        const string test = """
            Imports System
            Imports ThreadLocal = System.ThreadStaticAttribute

            Class Test
                <ThreadStatic>
                Private Shared field As Object

                <ThreadLocal>
                Private Shared aliasedField As Integer

                Shared Sub New()
                    field = New Object()
                End Sub
            End Class
            """;

        await VBVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task NonStaticFields_VisualBasic()
    {
        const string test = """
            Imports System

            Class Test
                <ThreadStatic>
                Private {|VSTHRD116:first|} As Object

                <Global.System.ThreadStaticAttribute>
                Private {|VSTHRD116:second|}, {|VSTHRD116:third|} As Integer

            End Class
            """;

        await VBVerify.VerifyAnalyzerAsync(test);
    }

    [Theory]
    [InlineData("Object", "New Object()")]
    [InlineData("Object", "Nothing")]
    [InlineData("Integer", "42")]
    [InlineData("Integer", "0")]
    public async Task InlineInitialization_VisualBasic(string type, string value)
    {
        string test = $$"""
            Imports System

            Class Test
                <ThreadStatic>
                Private Shared field As {{type}} {|VSTHRD117:= {{value}}|}
            End Class
            """;

        await VBVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task NonStaticInitializedFieldOnlyReportsNonStaticDiagnostic_VisualBasic()
    {
        const string test = """
            Imports System

            Class Test
                <ThreadStatic>
                Private {|VSTHRD116:field|} As Object = New Object()
            End Class
            """;

        await VBVerify.VerifyAnalyzerAsync(test);
    }
}
