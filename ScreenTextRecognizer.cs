using System.IO;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SkiaSharp;

namespace MabiLifeAssistant;

internal sealed class ScreenTextRecognizer : IDisposable
{
    private readonly RapidOcr _engine;
    private readonly object _engineLock = new();

    private ScreenTextRecognizer(RapidOcr engine) => _engine = engine;

    public static ScreenTextRecognizer Create()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var modelDirectory = Path.Combine(baseDirectory, "models", "v6");
        var modelSet = RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = Path.Combine(modelDirectory, "PP-OCRv6_det_small.onnx"),
            RecModelPath = Path.Combine(modelDirectory, "PP-OCRv6_rec_small.onnx"),
            KeysPath = Path.Combine(modelDirectory, "ppocrv6_small_dict.txt"),
            ClsModelPath = Path.Combine(baseDirectory, "models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx")
        };

        foreach (var path in new[] { modelSet.DetModelPath, modelSet.RecModelPath, modelSet.KeysPath })
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"RapidOCR 模型檔案遺失：{path}");
        }

        var engine = new RapidOcr();
        engine.InitModels(modelSet);
        return new ScreenTextRecognizer(engine);
    }

    public Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(BitmapSource source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var png = new MemoryStream();
        encoder.Save(png);
        var bytes = png.ToArray();

        return Task.Run<IReadOnlyList<RecognizedLine>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = SKBitmap.Decode(bytes)
                               ?? throw new InvalidOperationException("RapidOCR 無法解碼遊戲畫面。");
            lock (_engineLock)
            {
                var result = _engine.Detect(bitmap, RapidOcrOptions.PPOCRv6);
                return result.TextBlocks
                    .Where(block => !string.IsNullOrWhiteSpace(block.Text))
                    .Select(block => new RecognizedLine(block.Text.Trim(), ToBounds(block.BoxPoints)))
                    .Where(line => line.Bounds.Width > 0 && line.Bounds.Height > 0)
                    .ToArray();
            }
        }, cancellationToken);
    }

    private static System.Windows.Rect ToBounds(IReadOnlyList<SKPointI> points)
    {
        if (points.Count == 0)
            return System.Windows.Rect.Empty;

        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        return new System.Windows.Rect(minX, minY, maxX - minX, maxY - minY);
    }

    public static string Normalize(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"[\s\p{P}\p{S}]", string.Empty);

    public void Dispose() => _engine.Dispose();
}

internal sealed record RecognizedLine(string Text, System.Windows.Rect Bounds);
