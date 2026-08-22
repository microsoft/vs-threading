# VSTHRD013 Apply `CompletedTaskAttribute` only to immutable members

`Microsoft.VisualStudio.Threading.CompletedTaskAttribute` tells VSTHRD003 that a member always
produces a completed task. Applying it to a mutable member is unsafe because the member can later
be assigned an incomplete task.

## Examples of patterns that are flagged by this analyzer

```csharp
[CompletedTask]
private static Task CachedTask { get; set; } = Task.CompletedTask;
```

The diagnostic is reported on `[CompletedTask]`, not at each use of `CachedTask`. Consumers take
the attribute at face value and do not report VSTHRD003.

## Solution

Apply `[CompletedTask]` only to methods that always return completed tasks, `readonly` fields, or
non-ref get-only properties:

```csharp
[CompletedTask]
private static readonly Task CachedTask = Task.CompletedTask;
```

If the member must remain mutable, remove `[CompletedTask]`. VSTHRD003 will then analyze each use
normally.
