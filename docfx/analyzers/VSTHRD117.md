# VSTHRD117 Avoid inline initialization of `ThreadStatic` fields

A static field marked with `ThreadStaticAttribute` has a separate value on each thread. An inline initializer runs only on the thread that initializes the containing type, so the field has its default value on every other thread. This also applies to auto-property backing fields marked with `[field: ThreadStatic]`.

## Examples of patterns that are flagged by this analyzer

```csharp
class Example
{
    [ThreadStatic]
    private static object value = new object();
}
```

```vb
Class Example
    <ThreadStatic>
    Private Shared value As Object = New Object()
End Class
```

## Solution

Remove the inline initializer and initialize the value independently on each thread, typically by using lazy initialization at the point of use.

```csharp
class Example
{
    [ThreadStatic]
    private static object value;

    private static object Value => value ??= new object();
}
```

```vb
Class Example
    <ThreadStatic>
    Private Shared value As Object

    Private Shared ReadOnly Property Value As Object
        Get
            If value Is Nothing Then
                value = New Object()
            End If

            Return value
        End Get
    End Property
End Class
```

No code fix is offered because the correct per-thread initialization depends on how the field is used.

This rule is equivalent to .NET SDK rule [CA2019](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2019). When both analyzer packages are enabled, configure or suppress one of the duplicate diagnostics.
