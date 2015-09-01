namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class AsyncEventHandlerAnalyzerTests : DiagnosticVerifier
    {
        private DiagnosticResult[] CreateExpects(DiagnosticResultLocation[] locations)
        {
            var results = new DiagnosticResult[locations.Length];
            for (int i = 0; i < locations.Length; ++i)
            {
                results[i] = new DiagnosticResult
                {
                    Id = "VSSDK005",
                    SkipVerifyMessage = true,
                    Severity = DiagnosticSeverity.Warning,
                    Locations = new[] { locations[i] }
                };
            }

            return results;
        }

        protected override DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return new AsyncEventHandlerAnalyzer();
        }

        [TestMethod]
        public void ReportWarningIfInvokeAsyncEventHandlerDirectly()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    event AsyncEventHandler<T> handler1;
    event AsyncEventHandler<EventArgs> handler2;
    event AsyncEventHandler handler3;

    void F() {
        handler1(this, null);
        handler2(this, null);
        handler3(this, null);
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 12, 9),
                new DiagnosticResultLocation("Test0.cs", 13, 9),
                new DiagnosticResultLocation("Test0.cs", 14, 9),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void DoNotReportWarningIfAsyncEventHandlerIsInvokedByInvokeAsync()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.Threading {
class TplExtensions {
    static void InvokeAsync(AsyncEventHandler<EventArgs> handler2, AsyncEventHandler handler3) {
        handler2(null, null);
        handler3(null, null);
    }
}}
";
            VerifyCSharpDiagnostic(test);
        }

        [TestMethod]
        public void ReportWarningIfInvokeAsyncEventHandlerDirectlyViaInvoke()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    event AsyncEventHandler<T> handler1;
    event AsyncEventHandler<EventArgs> handler2;
    event AsyncEventHandler handler3;

    void F() {
        handler1.Invoke(this, null);
        handler2.Invoke(this, null);
        handler3.Invoke(this, null);
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 12, 9),
                new DiagnosticResultLocation("Test0.cs", 13, 9),
                new DiagnosticResultLocation("Test0.cs", 14, 9),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void ReportWarningIfInvokeAsyncEventHandlerDirectlyAsDelegateViaInvoke()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    AsyncEventHandler<T> handler1;
    AsyncEventHandler<EventArgs> handler2;
    AsyncEventHandler handler3;

    void F() {
        this.handler1.Invoke(this, null);
        this.handler2.Invoke(this, null);
        this.handler3.Invoke(this, null);
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 12, 9),
                new DiagnosticResultLocation("Test0.cs", 13, 9),
                new DiagnosticResultLocation("Test0.cs", 14, 9),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void ReportWarningIfInvokeAsyncEventHandlerDirectlyAndTheyAreLocalVariables()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F() {
        AsyncEventHandler<T> handler1;
        AsyncEventHandler<EventArgs> handler2;
        AsyncEventHandler handler3;

        handler1(this, null);
        handler2(this, null);
        handler3(this, null);
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 12, 9),
                new DiagnosticResultLocation("Test0.cs", 13, 9),
                new DiagnosticResultLocation("Test0.cs", 14, 9),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void ReportWarningIfInvokeAsyncEventHandlerDirectlyAndTheyAreProperties()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    AsyncEventHandler<T> handler1 { get; set; }
    AsyncEventHandler<EventArgs> handler2 { get; set; }
    AsyncEventHandler handler3 { get; set; }

    void F() {
        handler1(this, null);
        handler2(this, null);
        handler3(this, null);
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 12, 9),
                new DiagnosticResultLocation("Test0.cs", 13, 9),
                new DiagnosticResultLocation("Test0.cs", 14, 9),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void ReportWarningIfInvokeAsyncEventHandlerViaInvocationList()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    event AsyncEventHandler<T> handler1;
    event AsyncEventHandler<EventArgs> handler2;
    event AsyncEventHandler handler3;

    void F() {
        handler1.GetInvocationList().Cast<AsyncEventHandler<T>>().Select(h => h(this, null));
        handler2.GetInvocationList().Cast<AsyncEventHandler<EventArgs>>().Select(h => h(this, null));
        handler3.GetInvocationList().Cast<AsyncEventHandler>().Select(h => h(this, null));
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 12, 79),
                new DiagnosticResultLocation("Test0.cs", 13, 87),
                new DiagnosticResultLocation("Test0.cs", 14, 76),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void ReportWarningIfInvokeAsyncEventHandlerAsDelegate()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    AsyncEventHandler<T> handler1;
    AsyncEventHandler<EventArgs> handler2;
    AsyncEventHandler handler3;

    void F() {
        handler1(this, null);
        handler2(this, null);
        handler3(this, null);
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 12, 9),
                new DiagnosticResultLocation("Test0.cs", 13, 9),
                new DiagnosticResultLocation("Test0.cs", 14, 9),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void ReportWarningIfInvokeLazyAsyncEventHandlerAsDelegate()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    Lazy<AsyncEventHandler<T>> handler1;
    Lazy<AsyncEventHandler<EventArgs>> handler2;
    Lazy<AsyncEventHandler> handler3;

    void F() {
        handler1.Value(this, null);
        handler2.Value(this, null);
        handler3.Value(this, null);
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 12, 9),
                new DiagnosticResultLocation("Test0.cs", 13, 9),
                new DiagnosticResultLocation("Test0.cs", 14, 9),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void ReportWarningIfInvokeAsyncEventHandlerDirectlyAndTheyArePassedAsParameters()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F(AsyncEventHandler<T> handler1, AsyncEventHandler<EventArgs> handler2, AsyncEventHandler handler3) {
        handler1(this, null);
        handler2(this, null);
        handler3(this, null);
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 8, 9),
                new DiagnosticResultLocation("Test0.cs", 9, 9),
                new DiagnosticResultLocation("Test0.cs", 10, 9),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void ReportWarningIfInvokeAsyncEventHandlerDirectlyAndTheyArePassedAsArray()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F(AsyncEventHandler<T>[] handlers1, AsyncEventHandler<EventArgs>[] handlers2, AsyncEventHandler[] handlers3) {
        handlers1.Select(h => h(this, null));
        handlers2.Select(h => h(this, null));
        handlers3.Select(h => h(this, null));
    }
}
";
            var locations = new[]
            {
                new DiagnosticResultLocation("Test0.cs", 8, 31),
                new DiagnosticResultLocation("Test0.cs", 9, 31),
                new DiagnosticResultLocation("Test0.cs", 10, 31),
            };
            VerifyCSharpDiagnostic(test, CreateExpects(locations));
        }

        [TestMethod]
        public void DoNotReportWarningIfInvokeAsyncEventHandlerUsingInvokeAsync()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    event AsyncEventHandler<T> handler1;
    event AsyncEventHandler<EventArgs> handler2;
    event AsyncEventHandler handler3;

    void F() {
        handler1.InvokeAsync(this, null);
        handler2.InvokeAsync(this, null);
        handler3.InvokeAsync(this, null);
    }
}
";
            VerifyCSharpDiagnostic(test);
        }

        [TestMethod]
        public void DoNotReportWarningIfInvokeAsyncEventHandlerUsingInvokeAsyncAndTheyArePassedAsParameters()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F(AsyncEventHandler<T> handler1, AsyncEventHandler<EventArgs> handler2, AsyncEventHandler handler3) {
        handler1.InvokeAsync(this, null);
        handler2.InvokeAsync(this, null);
        handler3.InvokeAsync(this, null);
    }
}
";
            VerifyCSharpDiagnostic(test);
        }

        [TestMethod]
        public void DoNotReportWarningIfInvokeAsyncEventHandlerUsingInvokeAsyncAndTheyArePassedAsArray()
        {
            var test = @"
using System;
using System.Linq;
using Microsoft.VisualStudio.Threading;

class Test<T> where T : EventArgs {
    void F(AsyncEventHandler<T>[] handlers1, AsyncEventHandler<EventArgs>[] handlers2, AsyncEventHandler[] handlers3) {
        handlers1.Select(h => h.InvokeAsync(this, null));
        handlers2.Select(h => h.InvokeAsync(this, null));
        handlers3.Select(h => h.InvokeAsync(this, null));
    }
}
";
            VerifyCSharpDiagnostic(test);
        }
    }
}