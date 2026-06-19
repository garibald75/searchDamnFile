# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and this project follows semantic
versioning when tagged releases are published.

## Unreleased

### Added

- Export results to CSV: right-click the result list and choose **Export to CSV…** to
  save all current results with Type, Name, Size, Modified, Path, and Match columns.
  The file is UTF-8 encoded and opens correctly in Excel and other spreadsheet apps.
- Wildcard mask support in the Search field: `*` matches any sequence of characters,
  `?` matches a single character (e.g. `*.cs`, `report_202?_*`). Wildcards are
  detected automatically when the query contains `*` or `?` and Regex is not active.

### Changed

- The search field (formerly "Query") is now the first control on the top bar and
  receives focus automatically on launch, so you can start typing immediately.
- Renamed labels: "Query" → "Search", "Root" → "Start Search Path".
- Folders now bypass the content filter: when content search is active, folders
  still appear in results if their name matches, rather than being excluded.
- Content checkbox and text field are now flush on the same row with consistent
  vertical alignment.
- Search button now grays out visually while a search is running.

## [0.1.0] - 2026-06-17

### Added

- Content search now shows the first matching line from each file in a dedicated
  `Match` column in the results list, so you can see not just which file matched
  but also where.
- Column sorting: click any column header to sort results ascending or descending.
  The active sort column and direction are shown with ▲ / ▼ in the header.
- Press `Enter` in the result list to open the selected file or folder.
- Multi-select support for copy path and copy name: all selected rows are joined
  with newlines and placed on the clipboard.

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
