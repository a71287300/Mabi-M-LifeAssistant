using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabiLifeAssistant;

internal enum WorkState
{
    Unknown,
    Working,
    Idle
}

internal readonly record struct WorkStatePrediction(
    WorkState State,
    double Confidence,
    double Distance,
    System.Windows.Rect? Bounds);

/// <summary>
/// Small CPU-only nearest-centroid classifier for the in-game compass/work
/// indicator. Green visual candidates are located across the whole game frame;
/// the model then confirms whether each candidate is idle or working.
/// </summary>
internal sealed class WorkStateClassifier
{
    private const string ModelResourceName = "MabiLifeAssistant.models.work-state-model.json";
    private readonly WorkStateModel _model;

    private WorkStateClassifier(WorkStateModel model) => _model = model;

    public static WorkStateClassifier Create()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ModelResourceName)
                           ?? throw new InvalidOperationException("工作狀態模型遺失；請重新建置或發布助手。");
        var model = JsonSerializer.Deserialize<WorkStateModel>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })
                    ?? throw new InvalidOperationException("工作狀態模型格式無法讀取；請重新建置或發布助手。");
        var expectedFeatureLength = model.FeatureSize * model.FeatureSize * 3;
        if (model.FeatureSize <= 0 ||
            model.WorkingCentroid.Length != expectedFeatureLength ||
            model.IdleCentroid.Length != expectedFeatureLength)
        {
            throw new InvalidOperationException(
                $"工作狀態模型尺寸不正確：FeatureSize={model.FeatureSize}、" +
                $"工作特徵={model.WorkingCentroid.Length}、閒置特徵={model.IdleCentroid.Length}；" +
                "請重新建置或發布助手。");
        }
        return new WorkStateClassifier(model);
    }

    public WorkStatePrediction Predict(BitmapSource source)
    {
        var features = ExtractFeatures(source);
        return Classify(features, null);
    }

    private WorkStatePrediction Classify(double[] features, System.Windows.Rect? bounds)
    {
        var workingDistance = MeanSquaredDistance(features, _model.WorkingCentroid);
        var idleDistance = MeanSquaredDistance(features, _model.IdleCentroid);
        var nearest = Math.Min(workingDistance, idleDistance);
        var farthest = Math.Max(workingDistance, idleDistance);
        var margin = (farthest - nearest) / Math.Max(farthest, 0.000001);

        if (nearest > _model.MaxKnownDistance || margin < _model.MinimumMargin)
            return new WorkStatePrediction(WorkState.Unknown, margin, nearest, bounds);

        return workingDistance < idleDistance
            ? new WorkStatePrediction(WorkState.Working, margin, workingDistance, bounds)
            : new WorkStatePrediction(WorkState.Idle, margin, idleDistance, bounds);
    }

    private double[] ExtractFeatures(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        var cropSide = Math.Max(32, (int)Math.Round(converted.PixelHeight * _model.CropSide));
        cropSide = Math.Min(cropSide, Math.Min(converted.PixelWidth, converted.PixelHeight));
        var centerX = (int)Math.Round(converted.PixelWidth * _model.CenterX);
        var centerY = (int)Math.Round(converted.PixelHeight * _model.CenterY);
        var left = Math.Clamp(centerX - cropSide / 2, 0, converted.PixelWidth - cropSide);
        var top = Math.Clamp(centerY - cropSide / 2, 0, converted.PixelHeight - cropSide);
        return ExtractFeatures(pixels, converted.PixelWidth, converted.PixelHeight,
            new System.Windows.Rect(left, top, cropSide, cropSide));
    }

    private double[] ExtractFeatures(byte[] pixels, int width, int height, System.Windows.Rect bounds)
    {
        var side = Math.Clamp((int)Math.Round(bounds.Width), 1, Math.Min(width, height));
        var left = Math.Clamp((int)Math.Round(bounds.X), 0, width - side);
        var top = Math.Clamp((int)Math.Round(bounds.Y), 0, height - side);
        var features = new double[_model.FeatureSize * _model.FeatureSize * 3];
        var offset = 0;

        for (var y = 0; y < _model.FeatureSize; y++)
        {
            var sourceY = Math.Min(height - 1, top + (int)((y + 0.5) * side / _model.FeatureSize));
            for (var x = 0; x < _model.FeatureSize; x++)
            {
                var sourceX = Math.Min(width - 1, left + (int)((x + 0.5) * side / _model.FeatureSize));
                var pixelIndex = (sourceY * width + sourceX) * 4;
                features[offset++] = pixels[pixelIndex + 2] / 255d;
                features[offset++] = pixels[pixelIndex + 1] / 255d;
                features[offset++] = pixels[pixelIndex] / 255d;
            }
        }

        return features;
    }

    /// <summary>
    /// Searches the complete frame for the green circular HUD control. The
    /// color mask is only a fast candidate finder; classification still uses
    /// the trained working/idle model, so green scenery is rejected by the
    /// known-distance and confidence checks.
    /// </summary>
    public WorkStatePrediction PredictAnywhere(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width < 32 || height < 32)
            return new WorkStatePrediction(WorkState.Unknown, 0, double.MaxValue, null);

        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        // A loading/transition screen is also a busy game state. It has no
        // compass or stop square, so classify it before searching HUD
        // candidates and keep the assistant from treating the black screen
        // as an idle game scene.
        if (LooksLikeBusyTransitionFrame(pixels, width, height))
            return new WorkStatePrediction(WorkState.Working, 1, 0, null);

        var candidates = FindGreenCandidates(pixels, width, height).ToList();
        // Keep both proposals when their sizes differ. The green ring and the
        // white center can produce different bounding boxes for the same HUD
        // control, and the tighter white-centered crop is often the better
        // classifier input for the working state.
        candidates.AddRange(FindWhiteCandidates(pixels, width, height));

        WorkStatePrediction? bestKnown = null;
        WorkStatePrediction? bestAny = null;
        WorkStatePrediction? bestWorking = null;
        WorkStatePrediction? bestIdle = null;

        foreach (var bounds in candidates)
        {
            var signature = MeasureSignature(pixels, width, height, bounds);
            if (!signature.LooksLikeWorking && !signature.LooksLikeIdle)
                continue;

            var features = ExtractFeatures(pixels, width, height, bounds);
            var prediction = Classify(features, bounds);
            // A bright square in scenery can look superficially like the
            // working button. Require a close model match before allowing it
            // to override a compass candidate.
            if (signature.LooksLikeWorking && prediction.State == WorkState.Working
                && prediction.Distance > 0.06)
                continue;
            if (bestAny is null || prediction.Distance < bestAny.Value.Distance)
                bestAny = prediction;

            if (prediction.State != WorkState.Unknown
                && (bestKnown is null || prediction.Distance < bestKnown.Value.Distance))
                bestKnown = prediction;

            if (prediction.State == WorkState.Working && signature.LooksLikeWorking
                && (bestWorking is null || prediction.Distance < bestWorking.Value.Distance))
                bestWorking = prediction;

            if (prediction.State == WorkState.Idle && signature.LooksLikeIdle
                && (bestIdle is null || prediction.Distance < bestIdle.Value.Distance))
                bestIdle = prediction;
        }

        return bestWorking ?? bestIdle ?? bestKnown ?? bestAny
            ?? new WorkStatePrediction(WorkState.Unknown, 0, double.MaxValue, null);
    }

    private static bool LooksLikeBusyTransitionFrame(byte[] pixels, int width, int height)
    {
        const int sampleStep = 4;
        var samples = 0;
        var darkSamples = 0;
        var centralBrightSamples = 0;
        var centerLeft = width * 0.25;
        var centerRight = width * 0.75;
        var centerTop = height * 0.22;
        var centerBottom = height * 0.75;

        for (var y = 0; y < height; y += sampleStep)
        for (var x = 0; x < width; x += sampleStep)
        {
            var index = (y * width + x) * 4;
            var blue = pixels[index];
            var green = pixels[index + 1];
            var red = pixels[index + 2];
            var brightest = Math.Max(red, Math.Max(green, blue));
            var darkest = Math.Min(red, Math.Min(green, blue));
            samples++;
            if (brightest < 35)
                darkSamples++;

            if (x >= centerLeft && x <= centerRight
                && y >= centerTop && y <= centerBottom
                && brightest >= 160
                && brightest - darkest <= 125)
            {
                centralBrightSamples++;
            }
        }

        return samples > 0
            && darkSamples / (double)samples >= 0.90
            && centralBrightSamples >= 120;
    }

    private static VisualSignature MeasureSignature(byte[] pixels, int width, int height,
        System.Windows.Rect bounds)
    {
        var side = Math.Clamp((int)Math.Round(bounds.Width), 1, Math.Min(width, height));
        var left = Math.Clamp((int)Math.Round(bounds.X), 0, width - side);
        var top = Math.Clamp((int)Math.Round(bounds.Y), 0, height - side);
        var greenOuter = 0;
        var outerCount = 0;
        var whiteCenter = 0;
        var darkCenter = 0;
        var redCenter = 0;
        var centerCount = 0;

        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 20; x++)
        {
            var sourceX = Math.Min(width - 1, left + (int)((x + 0.5) * side / 20));
            var sourceY = Math.Min(height - 1, top + (int)((y + 0.5) * side / 20));
            var index = (sourceY * width + sourceX) * 4;
            var blue = pixels[index];
            var green = pixels[index + 1];
            var red = pixels[index + 2];
            var normalizedX = (x + 0.5) / 20d - 0.5;
            var normalizedY = (y + 0.5) / 20d - 0.5;
            var isGreen = green >= 115 && green - red >= 22 && green - blue >= -5;
            var isWhite = red >= 180 && green >= 180 && blue >= 180
                && Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) <= 75;
            var isDark = red < 125 && green < 145 && blue < 180;
            var isRed = red >= 135 && red - green >= 35 && red - blue >= 35;
            var isOuter = Math.Abs(normalizedX) >= 0.28 || Math.Abs(normalizedY) >= 0.28;
            var isCenter = Math.Abs(normalizedX) <= 0.22 && Math.Abs(normalizedY) <= 0.22;

            if (isOuter)
            {
                outerCount++;
                if (isGreen)
                    greenOuter++;
            }

            if (isCenter)
            {
                centerCount++;
                if (isWhite)
                    whiteCenter++;
                if (isDark)
                    darkCenter++;
                if (isRed)
                    redCenter++;
            }
        }

        return new VisualSignature(
            greenOuter / (double)Math.Max(1, outerCount),
            whiteCenter / (double)Math.Max(1, centerCount),
            darkCenter / (double)Math.Max(1, centerCount),
            redCenter / (double)Math.Max(1, centerCount));
    }

    private IReadOnlyList<System.Windows.Rect> FindWhiteCandidates(byte[] pixels, int width, int height)
    {
        var scale = Math.Max(1, (int)Math.Ceiling(width / 640d));
        var smallWidth = (width + scale - 1) / scale;
        var smallHeight = (height + scale - 1) / scale;
        var mask = new bool[smallWidth * smallHeight];

        for (var y = 0; y < smallHeight; y++)
        for (var x = 0; x < smallWidth; x++)
        {
            var sourceX = Math.Min(width - 1, x * scale + scale / 2);
            var sourceY = Math.Min(height - 1, y * scale + scale / 2);
            var index = (sourceY * width + sourceX) * 4;
            var blue = pixels[index];
            var green = pixels[index + 1];
            var red = pixels[index + 2];
            mask[y * smallWidth + x] = red >= 180 && green >= 180 && blue >= 180
                && Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) <= 75;
        }

        var expectedSide = Math.Max(32, height * _model.CropSide);
        var visited = new bool[mask.Length];
        var queue = new int[mask.Length];
        var candidates = new List<System.Windows.Rect>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || visited[start])
                continue;

            var queueCount = 0;
            var readIndex = 0;
            queue[queueCount++] = start;
            visited[start] = true;
            var minX = start % smallWidth;
            var maxX = minX;
            var minY = start / smallWidth;
            var maxY = minY;

            while (readIndex < queueCount)
            {
                var current = queue[readIndex++];
                var x = current % smallWidth;
                var y = current / smallWidth;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                for (var neighborY = Math.Max(0, y - 1); neighborY <= Math.Min(smallHeight - 1, y + 1); neighborY++)
                for (var neighborX = Math.Max(0, x - 1); neighborX <= Math.Min(smallWidth - 1, x + 1); neighborX++)
                {
                    var neighbor = neighborY * smallWidth + neighborX;
                    if (mask[neighbor] && !visited[neighbor])
                    {
                        visited[neighbor] = true;
                        queue[queueCount++] = neighbor;
                    }
                }
            }

            var componentWidth = (maxX - minX + 1) * scale;
            var componentHeight = (maxY - minY + 1) * scale;
            var maximumDimension = expectedSide * 0.55;
            var minimumDimension = expectedSide * 0.08;
            var aspect = componentWidth / (double)Math.Max(1, componentHeight);
            var fill = queueCount * scale * scale
                / (double)Math.Max(1, componentWidth * componentHeight);
            if (Math.Max(componentWidth, componentHeight) < minimumDimension
                || Math.Max(componentWidth, componentHeight) > maximumDimension
                || Math.Min(componentWidth, componentHeight) < minimumDimension
                || aspect < 0.35 || aspect > 4.0 || fill < 0.18)
                continue;

            var centerX = ((minX + maxX + 1) * scale) / 2d;
            var centerY = ((minY + maxY + 1) * scale) / 2d;
            var side = Math.Clamp((int)Math.Round(Math.Max(componentWidth, componentHeight) * 3.20),
                32, Math.Min(width, height));
            var left = Math.Clamp((int)Math.Round(centerX - side / 2d), 0, width - side);
            var top = Math.Clamp((int)Math.Round(centerY - side / 2d), 0, height - side);
            candidates.Add(new System.Windows.Rect(left, top, side, side));
        }

        return candidates;
    }

    private IReadOnlyList<System.Windows.Rect> FindGreenCandidates(byte[] pixels, int width, int height)
    {
        var scale = Math.Max(1, (int)Math.Ceiling(width / 640d));
        var smallWidth = (width + scale - 1) / scale;
        var smallHeight = (height + scale - 1) / scale;
        var mask = new bool[smallWidth * smallHeight];

        for (var y = 0; y < smallHeight; y++)
        for (var x = 0; x < smallWidth; x++)
        {
            var sourceX = Math.Min(width - 1, x * scale + scale / 2);
            var sourceY = Math.Min(height - 1, y * scale + scale / 2);
            var index = (sourceY * width + sourceX) * 4;
            var blue = pixels[index];
            var green = pixels[index + 1];
            var red = pixels[index + 2];
            mask[y * smallWidth + x] = green >= 115
                && green - red >= 22
                && green - blue >= -5;
        }

        var expectedSide = Math.Max(32, height * _model.CropSide);
        var minimumDimension = expectedSide * 0.35;
        var maximumDimension = expectedSide * 1.80;
        var visited = new bool[mask.Length];
        var queue = new int[mask.Length];
        var candidates = new List<System.Windows.Rect>();

        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || visited[start])
                continue;

            var queueCount = 0;
            var readIndex = 0;
            queue[queueCount++] = start;
            visited[start] = true;
            var minX = start % smallWidth;
            var maxX = minX;
            var minY = start / smallWidth;
            var maxY = minY;

            while (readIndex < queueCount)
            {
                var current = queue[readIndex++];
                var x = current % smallWidth;
                var y = current / smallWidth;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);

                for (var neighborY = Math.Max(0, y - 1); neighborY <= Math.Min(smallHeight - 1, y + 1); neighborY++)
                for (var neighborX = Math.Max(0, x - 1); neighborX <= Math.Min(smallWidth - 1, x + 1); neighborX++)
                {
                    var neighbor = neighborY * smallWidth + neighborX;
                    if (mask[neighbor] && !visited[neighbor])
                    {
                        visited[neighbor] = true;
                        queue[queueCount++] = neighbor;
                    }
                }
            }

            var componentWidth = (maxX - minX + 1) * scale;
            var componentHeight = (maxY - minY + 1) * scale;
            var componentArea = queueCount * scale * scale;
            var boxArea = Math.Max(1, componentWidth * componentHeight);
            var aspect = componentWidth / (double)Math.Max(1, componentHeight);
            var fill = componentArea / (double)boxArea;
            if (componentWidth < minimumDimension || componentHeight < minimumDimension
                || componentWidth > maximumDimension || componentHeight > maximumDimension
                || aspect < 0.55 || aspect > 1.80 || fill < 0.15)
                continue;

            var centerX = ((minX + maxX + 1) * scale) / 2d;
            var centerY = ((minY + maxY + 1) * scale) / 2d;
            // The working square and compass can have different apparent
            // sizes. Keep a small margin around each detected green circle
            // instead of forcing every candidate into the training crop size.
            var candidateSide = Math.Max(componentWidth, componentHeight) * 1.20;
            var side = Math.Clamp((int)Math.Round(candidateSide), 32, Math.Min(width, height));
            var left = Math.Clamp((int)Math.Round(centerX - side / 2d), 0, width - side);
            var top = Math.Clamp((int)Math.Round(centerY - side / 2d), 0, height - side);
            candidates.Add(new System.Windows.Rect(left, top, side, side));
        }

        return candidates;
    }

    private static double MeanSquaredDistance(double[] left, double[] right)
    {
        var sum = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            var difference = left[index] - right[index];
            sum += difference * difference;
        }
        return sum / left.Length;
    }

    private sealed class WorkStateModel
    {
        public int Version { get; set; }
        public int FeatureSize { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double CropSide { get; set; }
        public double[] WorkingCentroid { get; set; } = Array.Empty<double>();
        public double[] IdleCentroid { get; set; } = Array.Empty<double>();
        public double MaxKnownDistance { get; set; }
        public double MinimumMargin { get; set; }
    }

    private readonly record struct VisualSignature(
        double GreenOuter,
        double WhiteCenter,
        double DarkCenter,
        double RedCenter)
    {
        // The green ring fades during some working animations. The white stop
        // square remains stable, and the center is much brighter than the
        // dark compass face. Keep the model-distance check as the final gate
        // so unrelated white HUD text cannot become a work candidate.
        public bool LooksLikeWorking => GreenOuter >= 0.04
            && WhiteCenter >= 0.40
            && DarkCenter <= 0.30;
        public bool LooksLikeIdle => GreenOuter >= 0.20 && DarkCenter >= 0.40 && WhiteCenter >= 0.08;
    }
}
