---
name: testing
description: Provides guidance and instructions for writing, running and filtering tests.
user-invocable: false
---

**IMPORTANT**: This repository uses xUnit v3 with Microsoft.Testing.Platform (MTP v2). Traditional `--filter` syntax does NOT work. Use the options below instead.

* There should generally be one test project (under the `test` directory) per shipping project (under the `src` directory). Test projects are named after the project being tested with a `.Tests` suffix.
* Tests use xUnit v3 with Microsoft.Testing.Platform (MTP v2). The threading tests use xUnit's AOT runner on .NET 10 and the reflection-based runner on older targets.
* Run published NativeAOT tests with `tools/dotnet-test-cloud.ps1 -Configuration Release -IncludeNativeAOT` after `dotnet publish test -c Release`.

## Running Tests

**Run all tests**:
```bash
dotnet test --no-build -c Release
```

**Run tests for a specific test project**:
```bash
dotnet test --no-build -c Release
```

**Run a single test method**:
```bash
dotnet test --no-build -c Release -- --filter-method "ClassName.MethodName"
```

**Run all tests in a test class**:
```bash
dotnet test --no-build -c Release -- --filter-class "ClassName"
```

**Run tests with wildcard matching** (supports wildcards at beginning and/or end):
```bash
dotnet test --no-build -c Release -- --filter-method "*Pattern*"
```

**Run tests with a specific trait**:
```bash
dotnet test --no-build -c Release -- --filter-trait "TraitName=value"
```

**Run tests for a specific framework only**:
```bash
dotnet test --no-build -c Release --framework net8.0
```

**List all available tests without running them**:
```bash
dotnet run --no-build -c Release --framework net8.0 -- --list-tests
```

**Key points about test filtering with xUnit / MTP v2**:
- Options after `--` are passed to the test runner, not to `dotnet test`
- Use `--filter-method`, `--filter-class`, `--filter-namespace`, and `--filter-trait`
- Exclude unstable tests with `--filter-not-trait "TestCategory=FailsInCloudTest"`
- Traditional VSTest `--filter` expressions do NOT work
- Wildcards `*` are supported at the beginning and/or end of filter values
- See `--help` for query filter language for advanced scenarios
