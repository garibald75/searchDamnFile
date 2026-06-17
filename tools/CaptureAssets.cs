using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using SearchDamnFileStandalone;

internal static class CaptureAssets
{
    [STAThread]
    private static int Main(string[] args)
    {
        string outputDir = args.Length > 0 ? args[0] : Path.Combine("docs", "assets");
        string root = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();

        Directory.CreateDirectory(outputDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using (var form = new MainForm())
        {
            form.StartPosition = FormStartPosition.Manual;
            form.ShowInTaskbar = false;
            form.Size = new Size(1180, 760);
            form.Opacity = 0;
            form.Show();
            Pump();

            string tempDir = Path.Combine(Path.GetTempPath(), "SearchDamnFileCaptureAssets");
            Directory.CreateDirectory(tempDir);

            string main = Path.Combine(tempDir, "demo-00-main.png");
            string query = Path.Combine(tempDir, "demo-01-query.png");
            string results = Path.Combine(outputDir, "screenshot-results.png");
            string gif = Path.Combine(outputDir, "demo.gif");

            Capture(form, main);

            SetText(form, "_root", root);
            SetText(form, "_query", "Standalone");
            SetText(form, "_includeExt", "cs,md,cmd");
            SetStatus(form, "Ready to search", "0 results");
            Pump();
            Capture(form, query);

            PopulateDemoResults(form, root);
            SetStatus(form, "Done in 0.03s | scanned 9 | errors 0", "3 results");
            Pump();
            Capture(form, results);

            WriteAnimatedGif(new[] { main, query, results, results }, gif, 110);
        }

        return 0;
    }

    private static void PopulateDemoResults(MainForm form, string root)
    {
        var resultsField = Field(form, "_results");
        var listField = Field(form, "_list");
        var results = (List<SearchResult>)resultsField.GetValue(form);
        results.Clear();

        AddResult(results, root, "Standalone.cs");
        AddResult(results, root, "README.md");
        AddResult(results, root, "publish-win-x64.cmd");

        var list = (ListView)listField.GetValue(form);
        list.VirtualListSize = results.Count;
        list.Invalidate();
    }

    private static void AddResult(List<SearchResult> results, string root, string name)
    {
        string path = Path.Combine(root, name);
        var info = new FileInfo(path);

        results.Add(new SearchResult
        {
            Name = name,
            FullPath = path,
            IsDirectory = false,
            Size = info.Exists ? (long?)info.Length : null,
            ModifiedUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow
        });
    }

    private static void Capture(Form form, string path)
    {
        form.PerformLayout();
        Pump();

        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(path, ImageFormat.Png);
        }
    }

    private static void SetText(MainForm form, string fieldName, string value)
    {
        var box = (TextBox)Field(form, fieldName).GetValue(form);
        box.Text = value;
    }

    private static void SetStatus(MainForm form, string status, string stats)
    {
        ((Label)Field(form, "_status").GetValue(form)).Text = status;
        ((Label)Field(form, "_stats").GetValue(form)).Text = stats;
    }

    private static FieldInfo Field(MainForm form, string name)
    {
        return form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(20);
        }
    }

    private static void WriteAnimatedGif(string[] framePaths, string path, int delayCentiseconds)
    {
        var images = framePaths.Select(Image.FromFile).ToList();

        try
        {
            ImageCodecInfo encoder = ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/gif");
            var encoderParams = new EncoderParameters(1);
            var saveFlag = Encoder.SaveFlag;

            byte[] delay = new byte[4 * images.Count];
            for (int i = 0; i < images.Count; i++)
                BitConverter.GetBytes(delayCentiseconds).CopyTo(delay, 4 * i);

            images[0].SetPropertyItem(Property(0x5100, 4, delay));
            images[0].SetPropertyItem(Property(0x5101, 3, BitConverter.GetBytes((short)0)));

            encoderParams.Param[0] = new EncoderParameter(saveFlag, (long)EncoderValue.MultiFrame);
            images[0].Save(path, encoder, encoderParams);

            encoderParams.Param[0] = new EncoderParameter(saveFlag, (long)EncoderValue.FrameDimensionTime);
            for (int i = 1; i < images.Count; i++)
                images[0].SaveAdd(images[i], encoderParams);

            encoderParams.Param[0] = new EncoderParameter(saveFlag, (long)EncoderValue.Flush);
            images[0].SaveAdd(encoderParams);
        }
        finally
        {
            foreach (var image in images)
                image.Dispose();
        }
    }

    private static PropertyItem Property(int id, short type, byte[] value)
    {
        var item = (PropertyItem)FormatterServices.GetUninitializedObject(typeof(PropertyItem));
        item.Id = id;
        item.Type = type;
        item.Len = value.Length;
        item.Value = value;
        return item;
    }
}
