; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/master/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|-------
VSTHRD001 | Usage | Warning | VSTHRD001UseSwitchToMainThreadAsyncAnalyzer
VSTHRD002 | Usage | Warning | VSTHRD002UseJtfRunAnalyzer
VSTHRD003 | Usage | Warning | VSTHRD003UseJtfRunAsyncAnalyzer
VSTHRD004 | Usage | Error | VSTHRD004AwaitSwitchToMainThreadAsyncAnalyzer
VSTHRD010 | Usage | Warning | VSTHRD010MainThreadUsageAnalyzer
VSTHRD011 | Usage | Error | VSTHRD011UseAsyncLazyAnalyzer
VSTHRD012 | Usage | Warning | VSTHRD012SpecifyJtfWhereAllowed
VSTHRD100 | Usage | Warning | VSTHRD100AsyncVoidMethodAnalyzer
VSTHRD101 | Usage | Warning | VSTHRD101AsyncVoidLambdaAnalyzer
VSTHRD102 | Usage | Info | VSTHRD102AvoidJtfRunInNonPublicMembersAnalyzer
VSTHRD103 | Usage | Warning | VSTHRD103UseAsyncOptionAnalyzer
VSTHRD104 | Usage | Info | VSTHRD104OfferAsyncOptionAnalyzer
VSTHRD105 | Usage | Warning | VSTHRD105AvoidImplicitTaskSchedulerCurrentAnalyzer
VSTHRD106 | Usage | Warning | VSTHRD106UseInvokeAsyncForAsyncEventsAnalyzer
VSTHRD107 | Usage | Error | VSTHRD107AwaitTaskWithinUsingExpressionAnalyzer
VSTHRD108 | Usage | Warning | VSTHRD108AssertThreadRequirementUnconditionally
VSTHRD109 | Usage | Error | VSTHRD109AvoidAssertInAsyncMethodsAnalyzer
VSTHRD110 | Usage | Warning | VSTHRD110ObserveResultOfAsyncCallsAnalyzer
VSTHRD111 | Usage | Hidden | VSTHRD111UseConfigureAwaitAnalyzer
VSTHRD112 | Usage | Info | VSTHRD112ImplementSystemIAsyncDisposableAnalyzer
VSTHRD113 | Usage | Info | VSTHRD113CheckForSystemIAsyncDisposableAnalyzer
VSTHRD200 | Style | Warning | VSTHRD200UseAsyncNamingConventionAnalyzer