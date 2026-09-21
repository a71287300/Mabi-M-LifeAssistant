using RapidOcrNet;
using SkiaSharp;

if (args.Length != 1)
    throw new ArgumentException("Usage: RapidOcrSmoke <image>");

var modelDirectory = Path.Combine(AppContext.BaseDirectory, "models", "v6");
var modelSet = RapidOcrModelSet.PPOCRv6Small with
{
    DetModelPath = Path.Combine(modelDirectory, "PP-OCRv6_det_small.onnx"),
    RecModelPath = Path.Combine(modelDirectory, "PP-OCRv6_rec_small.onnx"),
    KeysPath = Path.Combine(modelDirectory, "ppocrv6_small_dict.txt"),
    ClsModelPath = Path.Combine(AppContext.BaseDirectory, "models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx")
};

using var ocr = new RapidOcr();
ocr.InitModels(modelSet);
using var bitmap = SKBitmap.Decode(args[0]) ?? throw new InvalidOperationException("Could not decode image.");
var result = ocr.Detect(bitmap, RapidOcrOptions.PPOCRv6);
foreach (var block in result.TextBlocks)
{
    var points = string.Join(" ", block.BoxPoints.Select(point => $"({point.X},{point.Y})"));
    var score = block.CharScores is null ? "n/a" : block.CharScores.Average().ToString("F3");
    Console.WriteLine($"{block.Text} | score={score} | {points}");
}
