# VSTHRD116 Use `ThreadStaticAttribute` only with static fields

`ThreadStaticAttribute` has no effect when applied to an instance field. Each object already has its own instance field, and the runtime does not create a separate value for each thread.

## Examples of patterns that are flagged by this analyzer

```csharp
class Example
{
    [ThreadStatic]
    private object value;
}
```

```vb
Class Example
    <ThreadStatic>
    Private value As Object
End Class
```

## Solution

If the value should be shared by all threads for each object, remove `ThreadStaticAttribute`. If each thread should have its own value, make the field static after confirming that changing it from per-instance state is appropriate.

```csharp
class Example
{
    [ThreadStatic]
    private static object value;
}
```

```vb
Class Example
    <ThreadStatic>
    Private Shared value As Object
End Class
```

No code fix is offered because removing the attribute and making the field static have different semantics.

This rule is equivalent to .NET SDK rule [CA2259](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2259). When both analyzer packages are enabled, configure or suppress one of the duplicate diagnostics.
