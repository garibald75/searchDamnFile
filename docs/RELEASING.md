# Releasing

This repository publishes Windows x64 builds from version tags.

## Checklist

1. Update `CHANGELOG.md`.
2. Build locally:

```cmd
publish-win-x64.cmd
```

3. Regenerate screenshots if the UI changed:

```cmd
tools\capture-assets.cmd
```

4. Commit the changes.
5. Create and push a tag:

```cmd
git tag v0.2.0
git push origin v0.2.0
```

The tag version must match `AppInfo.Version` in `Standalone.cs`, which is also
embedded in the executable and shown in the window title.

GitHub Actions will build the executable, zip it, and create or update the
GitHub release for that tag.
