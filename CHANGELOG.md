# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and this project follows semantic
versioning when tagged releases are published.

## [0.2.1] - 2026-07-04

### Changed

- Reverted the result list column order back to Type, Name, Size, Modified, Path,
  Match. The Type/Path swap introduced in 0.2.0 was not wanted.

## [0.2.0] - 2026-07-04

### Added

- Press `Enter` in the **Start Search Path** field to start a search, the same as
  the Search field. Previously only the Search field responded to `Enter`.
- Export results to CSV: right-click the result list and choose **Export to CSV…** to
  save all current results with Type, Name, Size, Modified, Path, and Match columns.
  The file is UTF-8 encoded and opens correctly in Excel and other spreadsheet apps.
- Wildcard mask support in the Search field: `*` matches any sequence of characters,
  `?` matches a single character (e.g. `*.cs`, `report_202?_*`). Wildcards are
  detected automatically when the query contains `*` or `?` and Regex is not active.

### Changed

- Directory traversal is now breadth-first instead of depth-first. When searching a
  whole drive such as `C:\`, matches from top-level user folders (Desktop, Documents,
  Pictures) surface quickly instead of the scan descending deep into system folders
  like `C:\Windows\WinSxS` first.
- The search field (formerly "Query") is now the first control on the top bar and
  receives focus automatically on launch, so you can start typing immediately.
- Renamed labels: "Query" → "Search", "Root" → "Start Search Path".
- Folders now bypass the content filter: when content search is active, folders
  still appear in results if their name matches, rather than being excluded.
- Content checkbox and text field are now flush on the same row with consistent
  vertical alignment.
- Search button now grays out visually while a search is running.
- Result list column order is now Path, Name, Size, Modified, Type, Match
  (Type and Path swapped) so the full path leads each row.

### Fixed

- Matched results are now flushed to the list on every progress update (about eight
  times per second) instead of only when 256 results accumulate or the search
  finishes. During long scans results now appear live instead of the list staying
  empty until a batch fills.

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
