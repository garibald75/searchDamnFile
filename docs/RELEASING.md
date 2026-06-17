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
git tag v0.1.0
git push origin v0.1.0
```

GitHub Actions will build the executable, zip it, and create or update the
GitHub release for that tag.
