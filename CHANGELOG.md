# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and this project follows semantic
versioning when tagged releases are published.

## Unreleased

### Added

- Content search now shows the first matching line from each file in a dedicated
  `Match` column in the results list, so you can see not just which file matched
  but also where.
- Column sorting: click any column header to sort results ascending or descending.
  The active sort column and direction are shown with ▲ / ▼ in the header.
- Press `Enter` in the result list to open the selected file or folder.
- Multi-select support for copy path and copy name: all selected rows are joined
  with newlines and placed on the clipboard.
- The `Folders` checkbox is automatically disabled while content search is active,
  making it clear that directory entries cannot be matched by content.

### Fixed

- Fixed crash when closing the app while a search is running. Background search
  callbacks now guard against posting to a disposed form window, and the form
  cancels any active search before closing.
- File-open errors (e.g. target deleted since the search ran) now show a warning
  dialog instead of crashing the app.
- Regex validation now runs before the search starts and shows a clear error dialog
  instead of reporting the error only in the status bar.
- File size is now read directly from the `FileInfo` object already returned by the
  directory enumerator, avoiding a redundant OS call per matched file.
- `CancellationTokenSource` is now properly disposed after each search completes,
  is stopped by the user, or fails with an error.

### Repo

- Prepared the repository for open source publication.
- Added README screenshots, demo GIF, issue templates, pull request template, and
  release automation.
