// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CSVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.CSharpCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.CSharpThreadStaticAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;
using VBVerify = Microsoft.VisualStudio.Threading.Analyzers.Tests.VisualBasicCodeFixVerifier<Microsoft.VisualStudio.Threading.Analyzers.VisualBasicThreadStaticAnalyzer, Microsoft.CodeAnalysis.Testing.EmptyCodeFixProvider>;

public class ThreadStaticAnalyzerTests
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
    public async Task FieldLikeEvents_CSharp()
    {
        const string test = """
            using System;
            using ThreadLocal = System.ThreadStaticAttribute;

            class Test
            {
                [field: ThreadStatic]
                private event EventHandler {|VSTHRD116:First|};

                [field: global::System.ThreadStaticAttribute]
                private event EventHandler {|VSTHRD116:Second|}, {|VSTHRD116:Third|};

                [field: ThreadLocal]
                private static event EventHandler StaticEvent;
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
    public async Task StaticConstructorAssignment_CSharp()
    {
        const string test = """
            using System;

            class Test
            {
                [ThreadStatic]
                private static object field;

                [ThreadStatic]
                private static int count;

                [ThreadStatic]
                private static State state;

                [ThreadStatic]
                private static ReferenceState referenceState;

                [field: ThreadStatic]
                private static object Property { get; set; }

                [field: ThreadStatic]
                private static event EventHandler Changed;

                private static event EventHandler OtherChanged;

                private static object otherField;

                static Test()
                {
                    {|VSTHRD117:field = new object()|};
                    {|VSTHRD117:field ??= new object()|};
                    {|VSTHRD117:Property = new object()|};
                    {|VSTHRD117:count++|};
                    {|VSTHRD117:--count|};
                    {|VSTHRD117:(otherField, (field, count)) = (new object(), (new object(), 1))|};
                    {|VSTHRD117:Changed += OnChanged|};
                    {|VSTHRD117:Changed -= OnChanged|};
                    Initialize({|VSTHRD117:out field|});
                    {|VSTHRD117:state.Value = 1|};
                    referenceState.Value = 1;
                    OtherChanged += OnChanged;
                    otherField = new object();

                    Action lambda = () => field = new object();

                    void LocalFunction()
                    {
                        field = new object();
                    }
                }

                private static void OnChanged(object sender, EventArgs args)
                {
                }

                private static void Initialize(out object value)
                {
                    value = new object();
                }

                private struct State
                {
                    internal int Value;
                }

                private class ReferenceState
                {
                    internal int Value;
                }

                private Test()
                {
                    field = new object();
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task FieldLikeEventAssignmentAcrossPartialType_CSharp()
    {
        const string eventDeclaration = """
            using System;

            partial class Test
            {
                [field: ThreadStatic]
                private static event EventHandler Changed;
            }
            """;
        const string typeInitializer = """
            using System;

            partial class Test
            {
                static Test()
                {
                    {|VSTHRD117:Changed += OnChanged|};
                }

                private static void OnChanged(object sender, EventArgs args)
                {
                }
            }
            """;
        var test = new CSVerify.Test();
        test.TestState.Sources.Add(eventDeclaration);
        test.TestState.Sources.Add(typeInitializer);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnattributedExplicitEventWithThreadStaticConstruction_CSharp()
    {
        const string test = """
            using System;

            class Test
            {
                private static event EventHandler Changed
                {
                    add
                    {
                        _ = new ThreadStaticAttribute();
                    }

                    remove
                    {
                    }
                }

                static Test()
                {
                    Changed += OnChanged;
                }

                private static void OnChanged(object sender, EventArgs args)
                {
                }
            }
            """;

        await CSVerify.VerifyAnalyzerAsync(test);
    }

    [Fact]
    public async Task AssignmentExpressionsInTypeInitializers_CSharp()
    {
        const string test = """
            using System;

            class Test
            {
                [ThreadStatic]
                private static int field;

                private static int OtherField = {|VSTHRD117:field = 1|};

                private static int OtherProperty { get; } = {|VSTHRD117:field++|};

                private static Func<int> Deferred = () => field++;
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

    [Fact]
    public async Task SharedConstructorAssignment_VisualBasic()
    {
        const string test = """
            Imports System

            Class Test
                <ThreadStatic>
                Private Shared field As Object

                <ThreadStatic>
                Private Shared stateData As State

                <ThreadStatic>
                Private Shared item As Integer

                <ThreadStatic>
                Private Shared array() As Integer

                Private Shared otherField As Object

                Shared Sub New()
                    {|VSTHRD117:field = New Object()|}
                    {|VSTHRD117:stateData.Value = 1|}
                    For Each {|VSTHRD117:item|} In {1, 2, 3}
                    Next
                    ReDim {|VSTHRD117:array|}(10)
                    otherField = New Object()

                    Dim initialize = Sub() field = New Object()
                End Sub

                Private Sub New()
                    field = New Object()
                End Sub

                Private Structure State
                    Friend Value As Integer
                End Structure
            End Class
            """;

        await VBVerify.VerifyAnalyzerAsync(test);
    }
}
