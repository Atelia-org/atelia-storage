# RBF Sized Append smoke

This standalone source consumer demonstrates the RBF sized-append API: it measures and inverts an append budget, starts two independent known-size Builders, stores each file's fixed-width `SizedPtr` in the other file, and verifies both references after closing and reopening the files read-only.

Run it from the repository root. By default the smoke test creates and removes a unique child directory under the system temp directory. Pass a parent directory to place the temporary RBF files on a chosen volume:

```powershell
dotnet run --project examples/RbfSizedAppendSmoke/RbfSizedAppendSmoke.csproj -c Release
dotnet run --project examples/RbfSizedAppendSmoke/RbfSizedAppendSmoke.csproj -c Release -- W:\RbfSizedAppendRun
```

The smoke test creates a unique child under the selected parent and deletes only that child when it exits. The project has one direct `ProjectReference`, to `src/Rbf/Rbf.csproj`. This exercises source integration; it is not evidence of NuGet package restore/consumption, throughput, or cross-file transaction guarantees.
