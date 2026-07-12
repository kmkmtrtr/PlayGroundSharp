# Repository guidance

- Use the .NET 10 SDK. Production libraries target `net10.0`; WPF targets `net10.0-windows`.
- Production projects live under `src/`; xUnit projects live under `tests/`.
- Build with `dotnet restore` and `dotnet build -c Debug`; test with `dotnet test -c Debug`.
- Keep nullable reference types enabled and resolve warnings instead of broadly suppressing them.
- Never execute submitted code in the WPF process; execution belongs in `PlayGroundSharp.Worker`.
- After changes, run affected tests and then the full Debug build/test suite.
- IPC changes must remain compatible or increment `ProtocolConstants.Version` and reject mismatches explicitly.
- Add package versions only to `Directory.Packages.props`; project files use versionless references.
