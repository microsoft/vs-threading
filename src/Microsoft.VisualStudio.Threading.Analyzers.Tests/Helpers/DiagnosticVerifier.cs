// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.Threading.Analyzers.Tests
{
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Xunit;
    using Xunit.Abstractions;

    /// <summary>
    /// Superclass of all Unit Tests for DiagnosticAnalyzers
    /// </summary>
    public abstract partial class DiagnosticVerifier
    {
        protected readonly ITestOutputHelper logger;

        protected DiagnosticVerifier(ITestOutputHelper logger)
        {
            this.logger = logger;
        }

        #region To be implemented by Test classes

        /// <summary>
        /// Get the CSharp analyzer being tested - to be implemented in non-abstract class
        /// </summary>
        protected virtual DiagnosticAnalyzer GetCSharpDiagnosticAnalyzer()
        {
            return null;
        }

        /// <summary>
        /// Get the CSharp analyzers being tested - to be implemented in non-abstract class
        /// </summary>
        protected virtual ImmutableArray<DiagnosticAnalyzer> GetCSharpDiagnosticAnalyzers()
        {
            return ImmutableArray.Create(this.GetCSharpDiagnosticAnalyzer());
        }

        /// <summary>
        /// Get the Visual Basic analyzer being tested (C#) - to be implemented in non-abstract class
        /// </summary>
        protected virtual DiagnosticAnalyzer GetBasicDiagnosticAnalyzer()
        {
            return null;
        }

        /// <summary>
        /// Get the Visual Basic analyzers being tested (C#) - to be implemented in non-abstract class
        /// </summary>
        protected virtual ImmutableArray<DiagnosticAnalyzer> GetBasicDiagnosticAnalyzers()
        {
            return ImmutableArray.Create(this.GetBasicDiagnosticAnalyzer());
        }
        #endregion

        #region Verifier wrappers

        /// <summary>
        /// Called to test a C# DiagnosticAnalyzer when applied on the single inputted string as a source
        /// Note: input a DiagnosticResult for each Diagnostic expected
        /// </summary>
        /// <param name="source">A class in the form of a string to run the analyzer on</param>
        /// <param name="expected"> DiagnosticResults that should appear after the analyzer is run on the source</param>
        protected void VerifyCSharpDiagnostic(string source, params DiagnosticResult[] expected)
        {
            this.VerifyDiagnostics(new[] { source }, LanguageNames.CSharp, this.GetCSharpDiagnosticAnalyzers(), hasEntrypoint: false, allowErrors: false, expected: expected);
        }

        /// <summary>
        /// Called to test a VB DiagnosticAnalyzer when applied on the single inputted string as a source
        /// Note: input a DiagnosticResult for each Diagnostic expected
        /// </summary>
        /// <param name="source">A class in the form of a string to run the analyzer on</param>
        /// <param name="expected">DiagnosticResults that should appear after the analyzer is run on the source</param>
        protected void VerifyBasicDiagnostic(string source, params DiagnosticResult[] expected)
        {
            this.VerifyDiagnostics(new[] { source }, LanguageNames.VisualBasic, this.GetBasicDiagnosticAnalyzers(), hasEntrypoint: false, allowErrors: false, expected: expected);
        }

        /// <summary>
        /// Called to test a C# DiagnosticAnalyzer when applied on the inputted strings as a source
        /// Note: input a DiagnosticResult for each Diagnostic expected
        /// </summary>
        /// <param name="sources">An array of strings to create source documents from to run the analyzers on</param>
        /// <param name="expected">DiagnosticResults that should appear after the analyzer is run on the sources</param>
        protected void VerifyCSharpDiagnostic(string[] sources, params DiagnosticResult[] expected)
        {
            this.VerifyCSharpDiagnostic(sources, hasEntrypoint: false, allowErrors: false, expected: expected);
        }

        /// <summary>
        /// Called to test a C# DiagnosticAnalyzer when applied on the inputted strings as a source
        /// Note: input a DiagnosticResult for each Diagnostic expected
        /// </summary>
        /// <param name="sources">An array of strings to create source documents from to run the analyzers on</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        /// <param name="allowErrors">A value indicating whether to fail the test if there are compiler errors in the code sample.</param>
        /// <param name="expected">DiagnosticResults that should appear after the analyzer is run on the sources</param>
        protected void VerifyCSharpDiagnostic(string[] sources, bool hasEntrypoint, bool allowErrors, params DiagnosticResult[] expected)
        {
            this.VerifyDiagnostics(sources, LanguageNames.CSharp, this.GetCSharpDiagnosticAnalyzers(), hasEntrypoint, allowErrors, expected);
        }

        /// <summary>
        /// Called to test a VB DiagnosticAnalyzer when applied on the inputted strings as a source
        /// Note: input a DiagnosticResult for each Diagnostic expected
        /// </summary>
        /// <param name="sources">An array of strings to create source documents from to run the analyzers on</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        /// <param name="allowErrors">A value indicating whether to fail the test if there are compiler errors in the code sample.</param>
        /// <param name="expected">DiagnosticResults that should appear after the analyzer is run on the sources</param>
        protected void VerifyBasicDiagnostic(string[] sources, bool hasEntrypoint, bool allowErrors, params DiagnosticResult[] expected)
        {
            this.VerifyDiagnostics(sources, LanguageNames.VisualBasic, this.GetBasicDiagnosticAnalyzers(), hasEntrypoint, allowErrors, expected);
        }

        /// <summary>
        /// General method that gets a collection of actual diagnostics found in the source after the analyzer is run,
        /// then verifies each of them.
        /// </summary>
        /// <param name="sources">An array of strings to create source documents from to run the analyzers on</param>
        /// <param name="language">The language of the classes represented by the source strings</param>
        /// <param name="analyzers">The analyzers to be run on the source code</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        /// <param name="allowErrors">A value indicating whether to fail the test if there are compiler errors in the code sample.</param>
        /// <param name="expected">DiagnosticResults that should appear after the analyzer is run on the sources</param>
        private void VerifyDiagnostics(string[] sources, string language, ImmutableArray<DiagnosticAnalyzer> analyzers, bool hasEntrypoint, bool allowErrors, params DiagnosticResult[] expected)
        {
            for (int i = 0; i < sources.Length; i++)
            {
                this.logger.WriteLine("File {0} content:", i + 1);
                this.LogFileContent(sources[i]);
            }

            var diagnostics = GetSortedDiagnostics(sources, language, analyzers, hasEntrypoint, allowErrors);
            this.VerifyDiagnosticResults(diagnostics, analyzers, expected);
        }

        protected void LogFileContent(string source)
        {
            using (var sr = new StringReader(source))
            {
                string line;
                int lineNumber = 1;
                while ((line = sr.ReadLine()) != null)
                {
                    this.logger.WriteLine("{0,2}: {1}", lineNumber++, line);
                }
            }
        }

        #endregion

        #region Actual comparisons and verifications

        /// <summary>
        /// Checks each of the actual Diagnostics found and compares them with the corresponding DiagnosticResult in the array of expected results.
        /// Diagnostics are considered equal only if the DiagnosticResultLocation, Id, Severity, and Message of the DiagnosticResult match the actual diagnostic.
        /// </summary>
        /// <param name="actualResults">The Diagnostics found by the compiler after running the analyzer on the source code</param>
        /// <param name="analyzers">The analyzers that were being run on the sources</param>
        /// <param name="expectedResults">Diagnostic Results that should have appeared in the code</param>
        private void VerifyDiagnosticResults(IEnumerable<Diagnostic> actualResults, ImmutableArray<DiagnosticAnalyzer> analyzers, params DiagnosticResult[] expectedResults)
        {
            int expectedCount = expectedResults.Count();
            int actualCount = actualResults.Count();

            string diagnosticsOutput = actualResults.Any() ? FormatDiagnostics(analyzers, actualResults.ToArray()) : "    NONE.";
            this.logger.WriteLine("Actual diagnostics:\n" + diagnosticsOutput);

            Assert.Equal(expectedCount, actualCount);

            for (int i = 0; i < expectedResults.Length; i++)
            {
                var actual = actualResults.ElementAt(i);
                var expected = expectedResults[i];

                if (expected.Line == -1 && expected.Column == -1)
                {
                    if (actual.Location != Location.None)
                    {
                        Assert.True(false,
                            string.Format("Expected:\nA project diagnostic with No location\nActual:\n{0}",
                            FormatDiagnostics(analyzers, actual)));
                    }
                }
                else
                {
                    VerifyDiagnosticLocation(analyzers, actual, actual.Location, expected.Locations.First());
                    var additionalLocations = actual.AdditionalLocations.ToArray();

                    if (additionalLocations.Length != expected.Locations.Length - 1)
                    {
                        Assert.True(false,
                            string.Format("Expected {0} additional locations but got {1} for Diagnostic:\r\n    {2}\r\n",
                                expected.Locations.Length - 1, additionalLocations.Length,
                                FormatDiagnostics(analyzers, actual)));
                    }

                    for (int j = 0; j < additionalLocations.Length; ++j)
                    {
                        VerifyDiagnosticLocation(analyzers, actual, additionalLocations[j], expected.Locations[j + 1]);
                    }
                }

                if (actual.Id != expected.Id)
                {
                    Assert.True(false,
                        string.Format("Expected diagnostic id to be \"{0}\" was \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.Id, actual.Id, FormatDiagnostics(analyzers, actual)));
                }

                if (actual.Severity != expected.Severity)
                {
                    Assert.True(false,
                        string.Format("Expected diagnostic severity to be \"{0}\" was \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.Severity, actual.Severity, FormatDiagnostics(analyzers, actual)));
                }

                if (!expected.SkipVerifyMessage && actual.GetMessage() != expected.Message)
                {
                    Assert.True(false,
                        string.Format("Expected diagnostic message to be \"{0}\" was \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.Message, actual.GetMessage(), FormatDiagnostics(analyzers, actual)));
                }
            }
        }

        /// <summary>
        /// Helper method to VerifyDiagnosticResult that checks the location of a diagnostic and compares it with the location in the expected DiagnosticResult.
        /// </summary>
        /// <param name="analyzers">The analyzers that were being run on the sources</param>
        /// <param name="diagnostic">The diagnostic that was found in the code</param>
        /// <param name="actual">The Location of the Diagnostic found in the code</param>
        /// <param name="expected">The DiagnosticResultLocation that should have been found</param>
        private static void VerifyDiagnosticLocation(ImmutableArray<DiagnosticAnalyzer> analyzers, Diagnostic diagnostic, Location actual, DiagnosticResultLocation expected)
        {
            var actualSpan = actual.GetLineSpan();

            Assert.True(actualSpan.Path == expected.Path || (actualSpan.Path != null && actualSpan.Path.Contains("Test0.") && expected.Path.Contains("Test.")),
                string.Format("Expected diagnostic to be in file \"{0}\" was actually in file \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                    expected.Path, actualSpan.Path, FormatDiagnostics(analyzers, diagnostic)));

            var actualLinePosition = actualSpan.StartLinePosition;

            // Only check line position if there is an actual line in the real diagnostic
            if (actualLinePosition.Line > 0)
            {
                if (actualLinePosition.Line + 1 != expected.Line)
                {
                    Assert.True(false,
                        string.Format("Expected diagnostic to be on line \"{0}\" was actually on line \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.Line, actualLinePosition.Line + 1, FormatDiagnostics(analyzers, diagnostic)));
                }

                if (expected.EndLine > -1 && actualSpan.EndLinePosition.Line + 1 != expected.EndLine)
                {
                    Assert.True(false,
                        string.Format("Expected diagnostic to end on line \"{0}\" was actually on line \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.EndLine, actualSpan.EndLinePosition.Line + 1, FormatDiagnostics(analyzers, diagnostic)));
                }
            }

            // Only check column position if there is an actual column position in the real diagnostic
            if (actualLinePosition.Character > 0)
            {
                if (actualLinePosition.Character + 1 != expected.Column)
                {
                    Assert.True(false,
                        string.Format("Expected diagnostic to start at column \"{0}\" was actually at column \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.Column, actualLinePosition.Character + 1, FormatDiagnostics(analyzers, diagnostic)));
                }

                if (expected.EndColumn > -1 && actualSpan.EndLinePosition.Character + 1 != expected.EndColumn)
                {
                    Assert.True(false,
                        string.Format("Expected diagnostic to end at column \"{0}\" was actually at column \"{1}\"\r\n\r\nDiagnostic:\r\n    {2}\r\n",
                            expected.EndColumn, actualSpan.EndLinePosition.Character + 1, FormatDiagnostics(analyzers, diagnostic)));
                }
            }
        }
        #endregion

        #region Formatting Diagnostics

        /// <summary>
        /// Helper method to format a Diagnostic into an easily readable string
        /// </summary>
        /// <param name="analyzers">The analyzers that this Verifier tests</param>
        /// <param name="diagnostics">The Diagnostics to be formatted</param>
        /// <returns>The Diagnostics formatted as a string</returns>
        protected static string FormatDiagnostics(ImmutableArray<DiagnosticAnalyzer> analyzers, params Diagnostic[] diagnostics)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < diagnostics.Length; ++i)
            {
                builder.AppendLine(diagnostics[i].ToString());
            }

            return builder.ToString();
        }
        #endregion
    }
}
