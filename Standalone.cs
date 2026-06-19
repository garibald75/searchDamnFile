using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SearchDamnFileStandalone
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal enum SearchTarget
    {
        Name,
        FullPath
    }

    internal sealed class SearchOptions
    {
        public string RootPath;
        public string Query;
        public SearchTarget Target;
        public bool IncludeFiles;
        public bool IncludeFolders;
        public bool CaseSensitive;
        public bool UseRegex;
        public bool WholeWord;
        public bool IncludeHiddenAndSystem;
        public bool FollowReparsePoints;
        public bool SkipVendorAndBuildDirs;
        public bool SearchContent;
        public string ContentQuery;
        public long ContentMaxBytes;
        public HashSet<string> IncludeExtensions;
        public HashSet<string> ExcludeExtensions;
        public string[] PathIncludes;
        public string[] PathExcludes;
        public int? MaxDepth;
        public long? MinBytes;
        public long? MaxBytes;
        public DateTime? ModifiedFromUtc;
        public DateTime? ModifiedToUtc;
        public int ResultLimit;
    }

    internal sealed class SearchResult
    {
        public string Name;
        public string FullPath;
        public bool IsDirectory;
        public long? Size;
        public DateTime ModifiedUtc;
        public string ContentMatch;
    }

    internal sealed class SearchProgress
    {
        public long Visited;
        public long Matched;
        public long Skipped;
        public long Errors;
        public string CurrentPath;
    }

    internal sealed class SearchSummary
    {
        public long Visited;
        public long Matched;
        public long Skipped;
        public long Errors;
        public TimeSpan Elapsed;
        public bool Limited;
    }

    internal sealed class SearchEngine
    {
        private static readonly HashSet<string> VendorDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".hg", ".svn", ".vs", "node_modules", "packages", "bin", "obj", "dist", "build", "out", "target"
        };

        public Task RunAsync(
            SearchOptions options,
            Action<List<SearchResult>> onBatch,
            Action<SearchProgress> onProgress,
            Action<SearchSummary> onComplete,
            Action<Exception> onError,
            CancellationToken token)
        {
            return Task.Factory.StartNew(delegate
            {
                try
                {
                    Run(options, onBatch, onProgress, onComplete, token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    onError(ex);
                }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static void Run(
            SearchOptions options,
            Action<List<SearchResult>> onBatch,
            Action<SearchProgress> onProgress,
            Action<SearchSummary> onComplete,
            CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            var stack = new Stack<DirFrame>();
            var batch = new List<SearchResult>(256);
            var matcher = BuildMatcher(options);
            var contentMatcher = BuildContentMatcher(options);
            long visited = 0;
            long matched = 0;
            long skipped = 0;
            long errors = 0;
            var progressClock = Stopwatch.StartNew();

            if (!Directory.Exists(options.RootPath))
                throw new DirectoryNotFoundException(options.RootPath);

            stack.Push(new DirFrame(options.RootPath, 0));

            while (stack.Count > 0 && matched < options.ResultLimit)
            {
                token.ThrowIfCancellationRequested();
                var frame = stack.Pop();

                if (progressClock.ElapsedMilliseconds >= 125)
                {
                    onProgress(new SearchProgress
                    {
                        Visited = visited,
                        Matched = matched,
                        Skipped = skipped,
                        Errors = errors,
                        CurrentPath = frame.Path
                    });
                    progressClock.Restart();
                }

                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(frame.Path).EnumerateFileSystemInfos();
                }
                catch
                {
                    errors++;
                    continue;
                }

                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (matched >= options.ResultLimit)
                        break;

                    visited++;

                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch
                    {
                        errors++;
                        continue;
                    }

                    bool isDirectory = (attributes & FileAttributes.Directory) == FileAttributes.Directory;
                    bool isReparse = (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;

                    if (!options.IncludeHiddenAndSystem &&
                        (((attributes & FileAttributes.Hidden) == FileAttributes.Hidden) ||
                         ((attributes & FileAttributes.System) == FileAttributes.System)))
                    {
                        skipped++;
                        continue;
                    }

                    if (isReparse && !options.FollowReparsePoints)
                    {
                        skipped++;
                        continue;
                    }

                    if (isDirectory)
                    {
                        if (ShouldTraverse(entry.Name, frame.Depth, options))
                            stack.Push(new DirFrame(entry.FullName, frame.Depth + 1));
                        else
                            skipped++;
                    }

                    if (isDirectory ? !options.IncludeFolders : !options.IncludeFiles)
                        continue;

                    if (!MatchesPathLists(entry.FullName, options))
                        continue;

                    string target = options.Target == SearchTarget.FullPath ? entry.FullName : entry.Name;
                    if (!matcher(target))
                        continue;

                    if (!MatchesExtension(entry, isDirectory, options))
                        continue;

                    long? size;
                    if (!MatchesMetadata(entry, isDirectory, options, out size))
                        continue;

                    string contentExcerpt;
                    if (!MatchesContent(entry, isDirectory, options, contentMatcher, size, out contentExcerpt))
                        continue;

                    matched++;
                    batch.Add(new SearchResult
                    {
                        Name = entry.Name,
                        FullPath = entry.FullName,
                        IsDirectory = isDirectory,
                        Size = isDirectory ? null : size,
                        ModifiedUtc = entry.LastWriteTimeUtc,
                        ContentMatch = contentExcerpt
                    });

                    if (batch.Count >= 256)
                        Flush(batch, onBatch);
                }
            }

            Flush(batch, onBatch);
            sw.Stop();
            onComplete(new SearchSummary
            {
                Visited = visited,
                Matched = matched,
                Skipped = skipped,
                Errors = errors,
                Elapsed = sw.Elapsed,
                Limited = matched >= options.ResultLimit
            });
        }

        private static Func<string, bool> BuildMatcher(SearchOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Query))
                return delegate { return true; };

            var regexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (!options.CaseSensitive)
                regexOptions |= RegexOptions.IgnoreCase;

            if (options.UseRegex)
            {
                var rx = new Regex(options.Query, regexOptions);
                return delegate(string value) { return rx.IsMatch(value); };
            }

            if (options.WholeWord)
            {
                var rx = new Regex(@"\b" + Regex.Escape(options.Query) + @"\b", regexOptions);
                return delegate(string value) { return rx.IsMatch(value); };
            }

            if (options.Query.IndexOfAny(new char[] { '*', '?' }) >= 0)
            {
                var pattern = "^" + Regex.Escape(options.Query).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                var rx = new Regex(pattern, regexOptions);
                return delegate(string value) { return rx.IsMatch(value); };
            }

            var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return delegate(string value) { return value.IndexOf(options.Query, comparison) >= 0; };
        }

        private static Func<string, bool> BuildContentMatcher(SearchOptions options)
        {
            if (!options.SearchContent || string.IsNullOrWhiteSpace(options.ContentQuery))
                return delegate { return true; };

            var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return delegate(string value) { return value.IndexOf(options.ContentQuery, comparison) >= 0; };
        }

        private static bool ShouldTraverse(string name, int depth, SearchOptions options)
        {
            if (options.MaxDepth.HasValue && depth >= options.MaxDepth.Value)
                return false;

            return !(options.SkipVendorAndBuildDirs && VendorDirs.Contains(name));
        }

        private static bool MatchesPathLists(string fullPath, SearchOptions options)
        {
            for (int i = 0; i < options.PathIncludes.Length; i++)
                if (fullPath.IndexOf(options.PathIncludes[i], StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

            for (int i = 0; i < options.PathExcludes.Length; i++)
                if (fullPath.IndexOf(options.PathExcludes[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;

            return true;
        }

        private static bool MatchesExtension(FileSystemInfo entry, bool isDirectory, SearchOptions options)
        {
            if (isDirectory)
                return true;

            var extension = Path.GetExtension(entry.Name);
            if (options.IncludeExtensions.Count > 0 && !options.IncludeExtensions.Contains(extension))
                return false;

            return !options.ExcludeExtensions.Contains(extension);
        }

        private static bool MatchesMetadata(FileSystemInfo entry, bool isDirectory, SearchOptions options, out long? size)
        {
            size = null;

            var modified = entry.LastWriteTimeUtc;
            if (options.ModifiedFromUtc.HasValue && modified < options.ModifiedFromUtc.Value)
                return false;

            if (options.ModifiedToUtc.HasValue && modified > options.ModifiedToUtc.Value)
                return false;

            if (isDirectory)
                return true;

            try
            {
                size = ((FileInfo)entry).Length;
            }
            catch
            {
                return false;
            }

            if (options.MinBytes.HasValue && size.Value < options.MinBytes.Value)
                return false;

            if (options.MaxBytes.HasValue && size.Value > options.MaxBytes.Value)
                return false;

            return true;
        }

        private static bool MatchesContent(FileSystemInfo entry, bool isDirectory, SearchOptions options, Func<string, bool> contentMatcher, long? size, out string excerpt)
        {
            excerpt = null;
            if (!options.SearchContent || string.IsNullOrWhiteSpace(options.ContentQuery))
                return true;

            if (isDirectory)
                return true;
            if (!size.HasValue || size.Value > options.ContentMaxBytes)
                return false;

            try
            {
                using (var stream = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024))
                using (var reader = new StreamReader(stream, true))
                {
                    var text = reader.ReadToEnd();
                    if (text.IndexOf('\0') >= 0 || !contentMatcher(text))
                        return false;
                    excerpt = FirstMatchingLine(text, contentMatcher);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string FirstMatchingLine(string text, Func<string, bool> contentMatcher)
        {
            int start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0) end = text.Length;
                var line = text.Substring(start, end - start).Trim('\r', '\t', ' ');
                if (line.Length > 0 && contentMatcher(line))
                    return line.Length > 200 ? line.Substring(0, 200) + "…" : line;
                start = end + 1;
            }
            return null;
        }

        private static void Flush(List<SearchResult> batch, Action<List<SearchResult>> onBatch)
        {
            if (batch.Count == 0)
                return;

            onBatch(new List<SearchResult>(batch));
            batch.Clear();
        }

        private sealed class DirFrame
        {
            public readonly string Path;
            public readonly int Depth;

            public DirFrame(string path, int depth)
            {
                Path = path;
                Depth = depth;
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox _root = new TextBox();
        private readonly TextBox _query = new TextBox();
        private readonly ComboBox _target = new ComboBox();
        private readonly Button _browse = new Button();
        private readonly Button _search = new Button();
        private readonly Button _stop = new Button();
        private readonly CheckBox _files = new CheckBox();
        private readonly CheckBox _folders = new CheckBox();
        private readonly CheckBox _subfolders = new CheckBox();
        private readonly CheckBox _case = new CheckBox();
        private readonly CheckBox _regex = new CheckBox();
        private readonly CheckBox _word = new CheckBox();
        private readonly CheckBox _hidden = new CheckBox();
        private readonly CheckBox _links = new CheckBox();
        private readonly CheckBox _skip = new CheckBox();
        private readonly CheckBox _content = new CheckBox();
        private readonly TextBox _includeExt = new TextBox();
        private readonly TextBox _excludeExt = new TextBox();
        private readonly TextBox _pathInclude = new TextBox();
        private readonly TextBox _pathExclude = new TextBox();
        private readonly NumericUpDown _depth = new NumericUpDown();
        private readonly NumericUpDown _minSize = new NumericUpDown();
        private readonly NumericUpDown _maxSize = new NumericUpDown();
        private readonly ComboBox _minUnit = new ComboBox();
        private readonly ComboBox _maxUnit = new ComboBox();
        private readonly DateTimePicker _from = new DateTimePicker();
        private readonly DateTimePicker _to = new DateTimePicker();
        private readonly TextBox _contentText = new TextBox();
        private readonly NumericUpDown _contentMax = new NumericUpDown();
        private readonly ComboBox _contentUnit = new ComboBox();
        private readonly NumericUpDown _limit = new NumericUpDown();
        private readonly Label _status = new Label();
        private readonly Label _stats = new Label();
        private readonly ListView _list = new ListView();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly List<SearchResult> _results = new List<SearchResult>();
        private readonly SearchEngine _engine = new SearchEngine();
        private CancellationTokenSource _cts;
        private int _generation;
        private int _dragIndex = -1;
        private int _sortColumn = -1;
        private bool _sortAscending = true;
        private static readonly string[] _columnHeaders = { "Type", "Name", "Size", "Modified", "Path", "Match" };

        public MainForm()
        {
            Text = "Search Damn File";
            Width = 1280;
            Height = 820;
            MinimumSize = new Size(1040, 640);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(244, 247, 250);
            ForeColor = Color.FromArgb(24, 32, 40);
            BuildUi();
            WireEvents();
        }

        private void BuildUi()
        {
            var rootPanel = new TableLayoutPanel();
            rootPanel.Dock = DockStyle.Fill;
            rootPanel.Padding = new Padding(12);
            rootPanel.RowCount = 4;
            rootPanel.ColumnCount = 1;
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 214));
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rootPanel.BackColor = BackColor;
            Controls.Add(rootPanel);

            rootPanel.Controls.Add(BuildTop(), 0, 0);
            rootPanel.Controls.Add(BuildFilters(), 0, 1);
            rootPanel.Controls.Add(BuildStatus(), 0, 2);
            rootPanel.Controls.Add(BuildList(), 0, 3);
            BuildMenu();
        }

        private Control BuildTop()
        {
            var p = PanelGrid(7, 2);
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            p.Controls.Add(Label("Search"), 0, 0);
            p.Controls.Add(Label("Target"), 1, 0);
            p.Controls.Add(Label("Start Search Path"), 3, 0);

            StyleText(_query);
            p.Controls.Add(_query, 0, 1);

            _target.DropDownStyle = ComboBoxStyle.DropDownList;
            _target.Items.Add("Name");
            _target.Items.Add("Full path");
            _target.SelectedIndex = 0;
            StyleCombo(_target);
            p.Controls.Add(_target, 1, 1);

            _root.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            StyleText(_root);
            p.Controls.Add(_root, 3, 1);

            _browse.Text = "Browse";
            StyleButton(_browse, false);
            p.Controls.Add(_browse, 4, 1);

            _search.Text = "Search";
            StyleButton(_search, true);
            p.Controls.Add(_search, 5, 1);

            _stop.Text = "Stop";
            _stop.Enabled = false;
            StyleButton(_stop, false);
            p.Controls.Add(_stop, 6, 1);

            return p;
        }

        private Control BuildFilters()
        {
            var p = PanelGrid(14, 4);
            for (int i = 0; i < 14; i++)
                p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 7.143F));
            for (int i = 0; i < 4; i++)
                p.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? 34 : 50));

            AddCheck(p, _files, "Files", true, 0, 0);
            AddCheck(p, _folders, "Folders", true, 1, 0);
            AddCheck(p, _subfolders, "Include subfolders", true, 2, 0, 2);
            AddCheck(p, _case, "Case", false, 4, 0);
            AddCheck(p, _regex, "Regex", false, 5, 0);
            AddCheck(p, _word, "Whole word", false, 6, 0, 2);
            AddCheck(p, _hidden, "Hidden/system", false, 8, 0, 2);
            AddCheck(p, _links, "Follow links", false, 10, 0, 2);
            AddCheck(p, _skip, "Skip vendor/build", true, 12, 0, 2);

            AddText(p, "Ext include", _includeExt, 0, 1, 4);
            AddText(p, "Ext exclude", _excludeExt, 4, 1, 3);
            AddText(p, "Path include", _pathInclude, 7, 1, 4);
            AddText(p, "Path exclude", _pathExclude, 11, 1, 3);

            AddNumber(p, "Max depth", _depth, 0, 999, 0, 0, 2, 2);
            AddSize(p, "Min size", _minSize, _minUnit, 2, 2, 3);
            AddSize(p, "Max size", _maxSize, _maxUnit, 5, 2, 3);
            AddDate(p, "Modified from", _from, 8, 2, 3);
            AddDate(p, "Modified to", _to, 11, 2, 3);

            var contentWrap = new TableLayoutPanel();
            contentWrap.Dock = DockStyle.Fill;
            contentWrap.RowCount = 2;
            contentWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            contentWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            contentWrap.Margin = new Padding(0, 0, 8, 0);
            _content.Text = "Content";
            _content.Checked = false;
            _content.Dock = DockStyle.Top;
            _content.Margin = new Padding(0, 3, 0, 0);
            _content.MinimumSize = new Size(0, 25);
            _content.ForeColor = Color.FromArgb(24, 32, 40);
            contentWrap.Controls.Add(_content, 0, 1);
            p.Controls.Add(contentWrap, 0, 3);
            p.SetColumnSpan(contentWrap, 1);
            AddText(p, "Text", _contentText, 1, 3, 7);
            AddSize(p, "Max text file", _contentMax, _contentUnit, 8, 3, 3);
            AddNumber(p, "Limit", _limit, 1, 1000000, 100000, 11, 3, 3);
            _contentMax.Value = 2;

            return p;
        }

        private Control BuildStatus()
        {
            var p = new TableLayoutPanel();
            p.Dock = DockStyle.Fill;
            p.ColumnCount = 2;
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            p.BackColor = BackColor;
            _status.Text = "Ready";
            _stats.Text = "0 results";
            _stats.TextAlign = ContentAlignment.MiddleRight;
            StyleLabel(_status);
            StyleLabel(_stats);
            p.Controls.Add(_status, 0, 0);
            p.Controls.Add(_stats, 1, 0);
            return p;
        }

        private Control BuildList()
        {
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.HideSelection = false;
            _list.VirtualMode = true;
            _list.BackColor = Color.White;
            _list.ForeColor = Color.FromArgb(24, 32, 40);
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.Columns.Add("Type", 70);
            _list.Columns.Add("Name", 290);
            _list.Columns.Add("Size", 110, HorizontalAlignment.Right);
            _list.Columns.Add("Modified", 165);
            _list.Columns.Add("Path", 380);
            _list.Columns.Add("Match", 240);
            _list.RetrieveVirtualItem += RetrieveVirtualItem;
            return _list;
        }

        private void BuildMenu()
        {
            _menu.Items.Add("Open", null, delegate { OpenSelected(); });
            _menu.Items.Add("Open containing folder", null, delegate { RevealSelected(); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Copy path", null, delegate { CopyPath(); });
            _menu.Items.Add("Copy name", null, delegate { CopyName(); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Export to CSV…", null, delegate { ExportCsv(); });
            _list.ContextMenuStrip = _menu;
        }

        private void WireEvents()
        {
            Load += delegate { _query.Focus(); };
            _search.EnabledChanged += delegate
            {
                _search.BackColor = _search.Enabled ? Color.FromArgb(25, 118, 210) : Color.FromArgb(200, 210, 220);
                _search.FlatAppearance.BorderColor = _search.Enabled ? Color.FromArgb(25, 118, 210) : Color.FromArgb(188, 198, 208);
                _search.ForeColor = _search.Enabled ? Color.White : Color.FromArgb(140, 150, 160);
            };
            _browse.Click += delegate { Browse(); };
            _search.Click += delegate { StartSearch(); };
            _stop.Click += delegate { StopSearch(); };
            _subfolders.CheckedChanged += delegate { _depth.Enabled = _subfolders.Checked; };
            _query.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    StartSearch();
                }
            };
            _list.MouseDoubleClick += delegate { OpenSelected(); };
            _list.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) OpenSelected(); };
            _list.ColumnClick += delegate(object sender, ColumnClickEventArgs e) { SortBy(e.Column); };
            _list.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                var item = _list.GetItemAt(e.X, e.Y);
                if (item != null)
                {
                    item.Selected = true;
                    _dragIndex = item.Index;
                }
            };
            _list.ItemDrag += delegate
            {
                int index = SelectedIndex();
                if (index < 0)
                    index = _dragIndex;
                if (index < 0 || index >= _results.Count)
                    return;
                var data = new DataObject(DataFormats.FileDrop, new string[] { _results[index].FullPath });
                DoDragDrop(data, DragDropEffects.Copy);
            };
        }

        private async void StartSearch()
        {
            StopSearch(false);

            SearchOptions options;
            try
            {
                options = CollectOptions();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid search", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _results.Clear();
            _list.VirtualListSize = 0;
            _status.Text = "Searching...";
            _stats.Text = "0 results";
            _search.Enabled = false;
            _stop.Enabled = true;
            _cts = new CancellationTokenSource();
            int generation = ++_generation;

            await _engine.RunAsync(
                options,
                delegate(List<SearchResult> batch) { SafeInvoke(delegate { if (generation == _generation) AddBatch(batch); }); },
                delegate(SearchProgress progress) { SafeInvoke(delegate { if (generation == _generation) UpdateProgress(progress); }); },
                delegate(SearchSummary summary) { SafeInvoke(delegate { if (generation == _generation) Finish(summary); }); },
                delegate(Exception error) { SafeInvoke(delegate { if (generation == _generation) Fail(error); }); },
                _cts.Token);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_cts != null) { _cts.Cancel(); _cts.Dispose(); _cts = null; }
            base.OnFormClosing(e);
        }

        private void SafeInvoke(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            try { BeginInvoke(action); }
            catch (ObjectDisposedException) { }
        }

        private void StopSearch()
        {
            StopSearch(true);
        }

        private void StopSearch(bool showStopped)
        {
            if (_cts == null)
                return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
            _generation++;
            _search.Enabled = true;
            _stop.Enabled = false;
            if (showStopped)
                _status.Text = "Stopped";
        }

        private SearchOptions CollectOptions()
        {
            string root = _root.Text.Trim();
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("Root path not found.");

            string query = _query.Text.Trim();
            if (_regex.Checked && !string.IsNullOrWhiteSpace(query))
            {
                try { new Regex(query); }
                catch (ArgumentException ex) { throw new ArgumentException("Invalid regular expression: " + ex.Message); }
            }

            return new SearchOptions
            {
                RootPath = root,
                Query = query,
                Target = _target.SelectedIndex == 1 ? SearchTarget.FullPath : SearchTarget.Name,
                IncludeFiles = _files.Checked,
                IncludeFolders = _folders.Checked,
                CaseSensitive = _case.Checked,
                UseRegex = _regex.Checked,
                WholeWord = _word.Checked,
                IncludeHiddenAndSystem = _hidden.Checked,
                FollowReparsePoints = _links.Checked,
                SkipVendorAndBuildDirs = _skip.Checked,
                SearchContent = _content.Checked,
                ContentQuery = _contentText.Text.Trim(),
                ContentMaxBytes = ToBytes(_contentMax.Value, Unit(_contentUnit)),
                IncludeExtensions = ParseExt(_includeExt.Text),
                ExcludeExtensions = ParseExt(_excludeExt.Text),
                PathIncludes = SplitList(_pathInclude.Text),
                PathExcludes = SplitList(_pathExclude.Text),
                MaxDepth = !_subfolders.Checked ? 0 : (_depth.Value == 0 ? (int?)null : (int)_depth.Value),
                MinBytes = _minSize.Value == 0 ? (long?)null : ToBytes(_minSize.Value, Unit(_minUnit)),
                MaxBytes = _maxSize.Value == 0 ? (long?)null : ToBytes(_maxSize.Value, Unit(_maxUnit)),
                ModifiedFromUtc = _from.Checked ? (DateTime?)_from.Value.Date.ToUniversalTime() : null,
                ModifiedToUtc = _to.Checked ? (DateTime?)_to.Value.Date.AddDays(1).AddTicks(-1).ToUniversalTime() : null,
                ResultLimit = (int)_limit.Value
            };
        }

        private void AddBatch(List<SearchResult> batch)
        {
            _results.AddRange(batch);
            _list.VirtualListSize = _results.Count;
            _list.Invalidate();
            _stats.Text = string.Format("{0:n0} results", _results.Count);
        }

        private void UpdateProgress(SearchProgress p)
        {
            _status.Text = string.Format("Scanned {0:n0} | matched {1:n0} | skipped {2:n0} | errors {3:n0}", p.Visited, p.Matched, p.Skipped, p.Errors);
        }

        private void Finish(SearchSummary s)
        {
            if (_cts != null) { _cts.Dispose(); _cts = null; }
            _search.Enabled = true;
            _stop.Enabled = false;
            _status.Text = string.Format("Done in {0:n2}s | scanned {1:n0} | errors {2:n0}{3}", s.Elapsed.TotalSeconds, s.Visited, s.Errors, s.Limited ? " | limit reached" : "");
            _stats.Text = string.Format("{0:n0} results", _results.Count);
        }

        private void Fail(Exception ex)
        {
            if (_cts != null) { _cts.Dispose(); _cts = null; }
            _search.Enabled = true;
            _stop.Enabled = false;
            _status.Text = ex.Message;
        }

        private void RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var r = _results[e.ItemIndex];
            var item = new ListViewItem(r.IsDirectory ? "DIR" : "FILE");
            item.SubItems.Add(r.Name);
            item.SubItems.Add(FormatSize(r.Size));
            item.SubItems.Add(r.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(r.FullPath);
            item.SubItems.Add(r.ContentMatch ?? "");
            e.Item = item;
        }

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose search root";
                if (Directory.Exists(_root.Text))
                    dialog.SelectedPath = _root.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _root.Text = dialog.SelectedPath;
            }
        }

        private void OpenSelected()
        {
            var r = Selected();
            if (r == null)
                return;
            try { Process.Start(new ProcessStartInfo(r.FullPath) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void RevealSelected()
        {
            var r = Selected();
            if (r == null)
                return;
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + r.FullPath + "\"") { UseShellExecute = true });
        }

        private void CopyPath()
        {
            var indices = _list.SelectedIndices;
            var paths = new List<string>(indices.Count);
            foreach (int i in indices)
                if (i >= 0 && i < _results.Count)
                    paths.Add(_results[i].FullPath);
            if (paths.Count > 0)
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }

        private void CopyName()
        {
            var indices = _list.SelectedIndices;
            var names = new List<string>(indices.Count);
            foreach (int i in indices)
                if (i >= 0 && i < _results.Count)
                    names.Add(_results[i].Name);
            if (names.Count > 0)
                Clipboard.SetText(string.Join(Environment.NewLine, names));
        }

        private void ExportCsv()
        {
            if (_results.Count == 0)
                return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Export results";
                dlg.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                dlg.DefaultExt = "csv";
                dlg.FileName = "search-results.csv";
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    using (var w = new StreamWriter(dlg.FileName, false, new System.Text.UTF8Encoding(true)))
                    {
                        w.WriteLine("Type,Name,Size,Modified,Path,Match");
                        foreach (var r in _results)
                        {
                            w.Write(CsvField(r.IsDirectory ? "Folder" : "File")); w.Write(',');
                            w.Write(CsvField(r.Name)); w.Write(',');
                            w.Write(r.Size.HasValue ? r.Size.Value.ToString() : ""); w.Write(',');
                            w.Write(CsvField(r.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))); w.Write(',');
                            w.Write(CsvField(r.FullPath)); w.Write(',');
                            w.WriteLine(CsvField(r.ContentMatch ?? ""));
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private static string CsvField(string value)
        {
            if (value.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) < 0)
                return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private void SortBy(int column)
        {
            if (_sortColumn == column)
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = column;
                _sortAscending = true;
            }

            for (int i = 0; i < _list.Columns.Count; i++)
                _list.Columns[i].Text = _columnHeaders[i];
            _list.Columns[column].Text = _columnHeaders[column] + (_sortAscending ? " ▲" : " ▼");

            _results.Sort(delegate(SearchResult a, SearchResult b)
            {
                int cmp;
                switch (column)
                {
                    case 0: cmp = a.IsDirectory.CompareTo(b.IsDirectory); break;
                    case 1: cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case 2: cmp = Nullable.Compare(a.Size, b.Size); break;
                    case 3: cmp = a.ModifiedUtc.CompareTo(b.ModifiedUtc); break;
                    default: cmp = string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase); break;
                }
                return _sortAscending ? cmp : -cmp;
            });
            _list.Invalidate();
        }

        private SearchResult Selected()
        {
            int i = SelectedIndex();
            return i >= 0 && i < _results.Count ? _results[i] : null;
        }

        private int SelectedIndex()
        {
            return _list.SelectedIndices.Count == 0 ? -1 : _list.SelectedIndices[0];
        }

        private static TableLayoutPanel PanelGrid(int cols, int rows)
        {
            var p = new TableLayoutPanel();
            p.Dock = DockStyle.Fill;
            p.ColumnCount = cols;
            p.RowCount = rows;
            p.Padding = new Padding(10);
            p.Margin = new Padding(0, 0, 0, 10);
            p.BackColor = Color.White;
            return p;
        }

        private static Label Label(string text)
        {
            var l = new Label();
            l.Text = text;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.BottomLeft;
            StyleLabel(l);
            return l;
        }

        private static void StyleLabel(Label l)
        {
            l.ForeColor = Color.FromArgb(82, 96, 112);
        }

        private static void StyleText(TextBox t)
        {
            t.Dock = DockStyle.Top;
            t.BackColor = Color.White;
            t.ForeColor = Color.FromArgb(24, 32, 40);
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Margin = new Padding(0, 3, 8, 0);
            t.MinimumSize = new Size(0, 25);
        }

        private static void StyleCombo(ComboBox c)
        {
            c.Dock = DockStyle.Top;
            c.BackColor = Color.White;
            c.ForeColor = Color.FromArgb(24, 32, 40);
            c.FlatStyle = FlatStyle.Flat;
            c.Margin = new Padding(0, 3, 8, 0);
            c.MinimumSize = new Size(0, 25);
        }

        private static void StyleButton(Button b, bool accent)
        {
            b.Dock = DockStyle.Fill;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = accent ? Color.FromArgb(25, 118, 210) : Color.FromArgb(188, 198, 208);
            b.BackColor = accent ? Color.FromArgb(25, 118, 210) : Color.FromArgb(238, 242, 246);
            b.ForeColor = accent ? Color.White : Color.FromArgb(24, 32, 40);
            b.Margin = new Padding(0, 2, 8, 2);
        }

        private static void AddCheck(TableLayoutPanel p, CheckBox box, string text, bool checkedValue, int col, int row)
        {
            AddCheck(p, box, text, checkedValue, col, row, 1);
        }

        private static void AddCheck(TableLayoutPanel p, CheckBox box, string text, bool checkedValue, int col, int row, int span)
        {
            box.Text = text;
            box.Checked = checkedValue;
            box.Dock = DockStyle.Fill;
            box.ForeColor = Color.FromArgb(24, 32, 40);
            box.Margin = new Padding(0, 0, 8, 0);
            p.Controls.Add(box, col, row);
            p.SetColumnSpan(box, span);
        }

        private static void AddText(TableLayoutPanel p, string label, TextBox text, int col, int row, int span)
        {
            var wrap = new TableLayoutPanel();
            wrap.Dock = DockStyle.Fill;
            wrap.RowCount = 2;
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            wrap.Margin = new Padding(0, 0, 8, 0);
            wrap.Controls.Add(SmallLabel(label), 0, 0);
            StyleText(text);
            wrap.Controls.Add(text, 0, 1);
            p.Controls.Add(wrap, col, row);
            p.SetColumnSpan(wrap, span);
        }

        private static void AddNumber(TableLayoutPanel p, string label, NumericUpDown n, decimal min, decimal max, decimal value, int col, int row, int span)
        {
            var wrap = new TableLayoutPanel();
            wrap.Dock = DockStyle.Fill;
            wrap.RowCount = 2;
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            wrap.Margin = new Padding(0, 0, 8, 0);
            wrap.Controls.Add(SmallLabel(label), 0, 0);
            n.Minimum = min;
            n.Maximum = max;
            n.Value = value;
            n.Dock = DockStyle.Top;
            n.BackColor = Color.White;
            n.ForeColor = Color.FromArgb(24, 32, 40);
            n.BorderStyle = BorderStyle.FixedSingle;
            n.Margin = new Padding(0, 3, 8, 0);
            n.MinimumSize = new Size(0, 25);
            wrap.Controls.Add(n, 0, 1);
            p.Controls.Add(wrap, col, row);
            p.SetColumnSpan(wrap, span);
        }

        private static void AddSize(TableLayoutPanel p, string label, NumericUpDown n, ComboBox unit, int col, int row)
        {
            AddSize(p, label, n, unit, col, row, 2);
        }

        private static void AddSize(TableLayoutPanel p, string label, NumericUpDown n, ComboBox unit, int col, int row, int span)
        {
            var wrap = new TableLayoutPanel();
            wrap.Dock = DockStyle.Fill;
            wrap.RowCount = 2;
            wrap.ColumnCount = 2;
            wrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            wrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            wrap.Margin = new Padding(0, 0, 8, 0);
            var labelControl = SmallLabel(label);
            wrap.Controls.Add(labelControl, 0, 0);
            wrap.SetColumnSpan(labelControl, 2);
            n.Minimum = 0;
            n.Maximum = 1000000;
            n.Dock = DockStyle.Top;
            n.BackColor = Color.White;
            n.ForeColor = Color.FromArgb(24, 32, 40);
            n.BorderStyle = BorderStyle.FixedSingle;
            n.Margin = new Padding(0, 3, 8, 0);
            n.MinimumSize = new Size(0, 25);
            unit.DropDownStyle = ComboBoxStyle.DropDownList;
            unit.Items.Add("B");
            unit.Items.Add("KB");
            unit.Items.Add("MB");
            unit.Items.Add("GB");
            unit.SelectedItem = "MB";
            StyleCombo(unit);
            wrap.Controls.Add(n, 0, 1);
            wrap.Controls.Add(unit, 1, 1);
            p.Controls.Add(wrap, col, row);
            p.SetColumnSpan(wrap, span);
        }

        private static void AddDate(TableLayoutPanel p, string label, DateTimePicker picker, int col, int row, int span)
        {
            var wrap = new TableLayoutPanel();
            wrap.Dock = DockStyle.Fill;
            wrap.RowCount = 2;
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            wrap.Margin = new Padding(0, 0, 8, 0);
            wrap.Controls.Add(SmallLabel(label), 0, 0);
            picker.Dock = DockStyle.Top;
            picker.Format = DateTimePickerFormat.Short;
            picker.ShowCheckBox = true;
            picker.Checked = false;
            picker.Margin = new Padding(0, 3, 8, 0);
            picker.MinimumSize = new Size(0, 25);
            wrap.Controls.Add(picker, 0, 1);
            p.Controls.Add(wrap, col, row);
            p.SetColumnSpan(wrap, span);
        }

        private static Label SmallLabel(string text)
        {
            var l = new Label();
            l.Text = text;
            l.Dock = DockStyle.Fill;
            l.ForeColor = Color.FromArgb(82, 96, 112);
            l.Font = new Font("Segoe UI", 7.8F);
            l.TextAlign = ContentAlignment.BottomLeft;
            return l;
        }

        private static HashSet<string> ParseExt(string value)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in SplitList(value))
            {
                if (item.Length == 0)
                    continue;
                set.Add(item[0] == '.' ? item : "." + item);
            }
            return set;
        }

        private static string[] SplitList(string value)
        {
            return value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(delegate(string x) { return x.Trim(); })
                .Where(delegate(string x) { return x.Length > 0; })
                .ToArray();
        }

        private static string Unit(ComboBox c)
        {
            return c.SelectedItem == null ? "MB" : c.SelectedItem.ToString();
        }

        private static long ToBytes(decimal value, string unit)
        {
            decimal m = 1;
            if (unit == "KB") m = 1024;
            else if (unit == "MB") m = 1024 * 1024;
            else if (unit == "GB") m = 1024 * 1024 * 1024;
            return (long)(value * m);
        }

        private static string FormatSize(long? bytes)
        {
            if (!bytes.HasValue)
                return "";

            double value = bytes.Value;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? string.Format("{0:n0} {1}", value, units[unit]) : string.Format("{0:n2} {1}", value, units[unit]);
        }
    }
}
