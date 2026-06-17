# Contributing

Thanks for helping improve Search Damn File.

## Development Setup

1. Use Windows.
2. Clone the repository.
3. Build with:

```cmd
publish-win-x64.cmd
```

The app has no NuGet dependencies. The source is intentionally kept in
`Standalone.cs` so the project remains easy to inspect, build, and distribute.

## Pull Requests

- Keep changes focused and small when possible.
- Preserve the no-dependency build unless a dependency is clearly worth it.
- Test the app manually after UI or search-behavior changes.
- Update `README.md` when user-facing behavior changes.
- Update `CHANGELOG.md` for notable fixes or features.
- Regenerate screenshots with `tools\capture-assets.cmd` when the UI changes.

## Code Style

- Keep compatibility with the .NET Framework compiler used by
  `publish-win-x64.cmd`.
- Prefer straightforward WinForms and BCL APIs.
- Use clear names and avoid broad refactors unrelated to the change.

## Reporting Bugs

When filing an issue, include:

- Windows version.
- What you searched for.
- The selected root folder type, for example local disk, network share, external
  drive, or synced folder.
- Expected behavior.
- Actual behavior.
- Any status-bar error count or exception message.
