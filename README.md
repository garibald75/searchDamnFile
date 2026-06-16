# Search Damn File

Tool desktop Windows in C# WinForms, senza pacchetti NuGet e senza SDK richiesto per la build locale.

## Build

```cmd
publish-win-x64.cmd
```

Exe finale:

```text
publish\SearchDamnFile.exe
```

## Funzioni

- Ricerca background con cancellazione.
- UI WinForms nativa.
- Filtri per file/cartelle, regex, case, whole word, hidden/system, symlink, extension include/exclude, path include/exclude, size, date, depth, limit.
- Ricerca opzionale nel contenuto dei file con limite massimo.
- Lista risultati virtualizzata.
- Doppio click: apre il file o la cartella.
- Click destro: open, open containing folder, copy path, copy name.
- Drag and drop verso altre app con formato nativo `FileDrop`.
