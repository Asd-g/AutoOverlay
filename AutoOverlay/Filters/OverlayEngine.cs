﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using AutoOverlay;
using AvsFilterNet;

[assembly: AvisynthFilterClass(
    typeof(OverlayEngine),
    nameof(OverlayEngine),
    "cc[StatFile]s" +
    "[BackwardFrames]i[ForwardFrames]i[SourceMask]c[OverlayMask]c" +
    "[MaxDiff]f[MaxDiffIncrease]f[MaxDeviation]f[Stabilize]b" +
    "[Configs]c[Downsize]s[Upsize]s[Rotate]s[Editor]b[Mode]s[Debug]b",
    MtMode.SERIALIZED)]
namespace AutoOverlay
{
    public class OverlayEngine : OverlayFilter
    {
        [AvsArgument(Required = true)]
        public Clip Source { get; private set; }

        [AvsArgument(Required = true)]
        public Clip Overlay { get; private set; }

        [AvsArgument]
        public string StatFile { get; set; }

        [AvsArgument(Min = 0, Max = 100)]
        public int BackwardFrames { get; private set; } = 3;

        [AvsArgument(Min = 0, Max = 100)]
        public int ForwardFrames { get; private set; } = 3;

        [AvsArgument]
        public Clip SourceMask { get; private set; }

        [AvsArgument]
        public Clip OverlayMask { get; private set; }

        [AvsArgument(Min = 0)]
        public double MaxDiff { get; private set; } = 5;

        [AvsArgument(Min = 0)]
        public double MaxDiffIncrease { get; private set; } = 1;

        [AvsArgument(Min = 0)]
        public double MaxDeviation { get; private set; } = 1;

        [AvsArgument]
        public bool Stabilize { get; private set; } = true;

        [AvsArgument]
        public Clip Configs { get; private set; }

        [AvsArgument]
        public string Downsize { get; private set; } = OverlayUtils.DEFAULT_RESIZE_FUNCTION;

        [AvsArgument]
        public string Upsize { get; private set; } = OverlayUtils.DEFAULT_RESIZE_FUNCTION;

        [AvsArgument]
        public string Rotate { get; private set; } = "BilinearRotate";

        [AvsArgument]
        public bool Editor { get; private set; }

        [AvsArgument]
        public OverlayEngineMode Mode { get; private set; } = OverlayEngineMode.DEFAULT;

        [AvsArgument]
        public override bool Debug { get; protected set; }

        public IOverlayStat OverlayStat { get; private set; }

        public ExtraVideoInfo SrcInfo { get; private set; }
        public ExtraVideoInfo OverInfo { get; private set; }

        private double correctionDistance = 0;
        private int correctionSteps = 2;

        private readonly ConcurrentDictionary<Tuple<OverlayInfo, int>, OverlayInfo> repeatCache = new ConcurrentDictionary<Tuple<OverlayInfo, int>, OverlayInfo>();
        private readonly ConcurrentDictionary<int, OverlayInfo> overlayCache = new ConcurrentDictionary<int, OverlayInfo>();

        public event EventHandler<FrameEventArgs> CurrentFrameChanged;

        private static OverlayEditor form;

#if DEBUG
        Stopwatch totalWatch = new Stopwatch();
        Stopwatch diffWatch = new Stopwatch();
        Stopwatch extraWatch = new Stopwatch();
#endif

        protected override void Initialize(AVSValue args)
        {
            base.Initialize(args);
            SrcInfo = Source.GetVideoInfo();
            OverInfo = Overlay.GetVideoInfo();
            if ((SrcInfo.ColorSpace ^ OverInfo.ColorSpace).HasFlag(ColorSpaces.CS_PLANAR))
                throw new AvisynthException("Both clips must be in planar or RGB color space");
            if (SrcInfo.ColorSpace.GetBitDepth() != OverInfo.ColorSpace.GetBitDepth())
                throw new AvisynthException("Both clips must have the same bit depth");
            MaxDeviation /= 100.0;

            var vi = GetVideoInfo();
            vi.num_frames = Math.Min(SrcInfo.FrameCount, OverInfo.FrameCount);
            SetVideoInfo(ref vi);

            OverlayStat = new FileOverlayStat(StatFile);
            var cacheSize = ForwardFrames + BackwardFrames + 1;
            var cacheKey = StaticEnv.GetEnv2() == null ? CacheType.CACHE_25_ALL : CacheType.CACHE_GENERIC;
            Source.SetCacheHints(cacheKey, cacheSize);
            Overlay.SetCacheHints(cacheKey, cacheSize);
            SourceMask?.SetCacheHints(cacheKey, cacheSize);
            OverlayMask?.SetCacheHints(cacheKey, cacheSize);
            if (Editor)
            {
                var activeForm = Form.ActiveForm;
                form?.Close();
                form = new OverlayEditor(this, StaticEnv);
                form.Show(activeForm);
            }
        }

        protected override VideoFrame GetFrame(int n)
        {
            var info = GetOverlayInfo(n);
            CurrentFrameChanged?.Invoke(this, new FrameEventArgs(n));
            var frame = Debug ? GetSubtitledFrame(this + "\n" + info) : base.GetFrame(n);
            StaticEnv.MakeWritable(frame);
            info.ToFrame(frame);
            return frame;
        }

        private OverlayInfo Repeat(OverlayInfo testInfo, int n)
        {
            return repeatCache.GetOrAdd(new Tuple<OverlayInfo, int>(testInfo, n), key => RepeatImpl(key.Item1, key.Item2));
        }

        private OverlayInfo AutoOverlay(int n)
        {
            return overlayCache.GetOrAdd(n, key => AutoOverlayImpl(n));
        }

        public OverlayInfo GetOverlayInfo(int n)
        {
            if (Mode == OverlayEngineMode.ERASE)
            {
                OverlayStat[n] = null;
                return new OverlayInfo
                {
                    FrameNumber = n,
                    Width = OverInfo.Width,
                    Height = OverInfo.Height,
                    Diff = -1
                };
            }
            var existed = OverlayStat[n];
            if (existed == null && Mode == OverlayEngineMode.READONLY)
            {
                return new OverlayInfo
                {
                    FrameNumber = n,
                    Width = OverInfo.Width,
                    Height = OverInfo.Height,
                    Diff = -1
                };
            }
            if (existed != null)
            {
                if (Mode == OverlayEngineMode.UPDATE)
                {
                    var repeated = Repeat(existed, n);
                    if (Math.Abs(repeated.Diff - existed.Diff) > double.Epsilon)
                        return OverlayStat[n] = repeated;
                }
                return existed;
            }
            var info = GetOverlayInfoImpl(n, out var sb);
            Log(sb.ToString());
            return info;
        }

        private double StdDev(IEnumerable<OverlayInfo> sample)
        {
            var mean = Mean(sample);
            return Math.Sqrt(sample.Sum(p => Math.Pow(p.Diff - mean, 2)));
        }

        private double Mean(IEnumerable<OverlayInfo> sample)
        {
            return sample.Sum(p => p.Diff) / sample.Count();
        }

        private bool CheckDev(IEnumerable<OverlayInfo> sample)
        {
            var mean = Mean(sample);
            return sample.All(p => p.Diff - mean <= MaxDiffIncrease);
        }

        private OverlayInfo GetOverlayInfoImpl(int n, out StringBuilder log)
        {
            log = new StringBuilder();
            log.AppendLine($"Frame: {n}");

            if (BackwardFrames == 0 || n < BackwardFrames) goto simple;
            var prevInfo = n > 0 ? OverlayStat[n - 1] : null;
            var prevFrames = Enumerable.Range(0, n)
                .Reverse().Select(p => OverlayStat[p])
                .TakeWhile(p => p != null && p.FrameNumber >= n - BackwardFrames && p.Diff <= MaxDiff && p.Equals(prevInfo)).ToArray();
            var prevFramesCount = Math.Min(prevFrames.Length, BackwardFrames);

            log.AppendLine($"Prev frames: {prevFramesCount}");

            if (prevFramesCount == BackwardFrames)
            {
                log.AppendLine($"Analyze prev frames info:\n{prevFrames.First()}");

                var info = Repeat(prevFrames.First(), n);

                if (info.Diff <= MaxDiff && CheckDev(prevFrames.Append(info)))
                {
                    log.AppendLine($"Repeated diff: {info.Diff:F3} is OK");
                    if (ForwardFrames > 0)
                    {
                        log.AppendLine($"Analyze next frames: {ForwardFrames}");
                        for (var nextFrame = n + 1;
                            nextFrame <= n + ForwardFrames && nextFrame < GetVideoInfo().num_frames;
                            nextFrame++)
                        {
                            log.AppendLine($"Next frame: {nextFrame}");
                            var stat = OverlayStat[nextFrame];
                            if (stat != null)
                            {
                                log.AppendLine($"Existed info found:\n{stat}");
                                if (stat.Equals(info))
                                {
                                    log.AppendLine($"Existed info is equal");
                                    if (stat.Diff <= MaxDiff && CheckDev(prevFrames.Append(stat)))
                                    {
                                        log.AppendLine($"Existed info diff {stat.Diff:F3} is OK");
                                        continue;
                                    }
                                    log.AppendLine($"Existed info diff {stat.Diff:F3} is not OK");
                                    goto simple;
                                }
                                if (stat.NearlyEquals(info, OverInfo.Size, MaxDeviation))
                                {
                                    log.AppendLine($"Existed info is nearly equal. Pan&scan mode.");
                                    goto simple;
                                }
                                break;
                            }
                            stat = Repeat(info, nextFrame);
                            if (stat.Diff > MaxDiff || !CheckDev(prevFrames.Append(stat)))
                            {
                                log.AppendLine($"Repeated info diff {stat.Diff:F3} is not OK");
                                stat = AutoOverlay(nextFrame);
                                log.AppendLine($"Own info: {stat}");
                                if (stat.NearlyEquals(info, OverInfo.Size, MaxDeviation))
                                {
                                    log.AppendLine($"Own info is nearly equal. Pan&scan mode.");
                                    goto simple;
                                }
                                log.AppendLine($"Next scene detected");
                                break;
                            }
                            log.AppendLine($"Repeated info diff: {stat.Diff:F3} is OK");
                        }
                    }
                    return OverlayStat[n] = info;
                }
                log.AppendLine($"Repeated diff: {info.Diff:F3} is not OK");
            }
            if (Stabilize)
            {
                var info = AutoOverlay(n).Clone();
                if (info.Diff > MaxDiff)
                    goto simple;
                prevFrames = prevFrames.TakeWhile(p => p.Equals(info)).ToArray();
                prevFramesCount = Math.Min(prevFrames.Length, BackwardFrames);
                var stabilizeFrames = new List<OverlayInfo> { info };
                for (var nextFrame = n + 1;
                    nextFrame < n + BackwardFrames - prevFramesCount &&
                    nextFrame < GetVideoInfo().num_frames;
                    nextFrame++)
                {
                    if (OverlayStat[nextFrame] != null)
                        goto simple;
                    var statOwn = AutoOverlay(nextFrame);
                    var statRepeated = Repeat(info, nextFrame);
                    stabilizeFrames.Add(statOwn);
                    if (statRepeated.Diff > MaxDiff || !CheckDev(prevFrames.Concat(stabilizeFrames)))// || !statOwn.NearlyEquals(statRepeated, delta))
                        goto simple;
                }
                if (prevFrames.Length == 0)
                {
                    var averageInfo = stabilizeFrames.Distinct()
                        .Select(p => new { Info = p, Count = stabilizeFrames.Count(p.Equals) })
                        .OrderByDescending(p => p.Count)
                        .ThenBy(p => p.Info.Diff)
                        .First()
                        .Info;

                    stabilizeFrames.Clear();
                    for (var frame = n; frame < n + BackwardFrames - prevFramesCount && frame < GetVideoInfo().num_frames; frame++)
                    {
                        var stabInfo = Repeat(averageInfo, frame);
                        stabilizeFrames.Add(stabInfo);
                        if (stabInfo.Diff > MaxDiff || !CheckDev(prevFrames.Concat(stabilizeFrames)))
                            goto simple;
                    }
                    info.CopyFrom(averageInfo);
                }
                for (var nextFrame = n + BackwardFrames - prevFramesCount;
                    nextFrame < n + BackwardFrames - prevFramesCount + ForwardFrames &&
                    nextFrame < GetVideoInfo().num_frames;
                    nextFrame++)
                {
                    var stat = OverlayStat[nextFrame];
                    if (stat != null)
                    {
                        if (stat.Equals(info))
                        {
                            stabilizeFrames.Add(stat);
                            if (stat.Diff <= MaxDiff && CheckDev(prevFrames.Concat(stabilizeFrames)))
                                continue;
                            goto simple;
                        }
                        if (stat.NearlyEquals(info, OverInfo.Size, MaxDeviation))
                        {
                            goto simple;
                        }
                        break;
                    }
                    stat = Repeat(info, nextFrame);
                    stabilizeFrames.Add(stat);
                    if (stat.Diff > MaxDiff || !CheckDev(prevFrames.Concat(stabilizeFrames)))
                    {
                        stat = AutoOverlay(nextFrame);
                        if (stat.NearlyEquals(info, OverInfo.Size, MaxDeviation))
                            goto simple;
                        break;
                    }
                }
                for (var frame = n;
                    frame <= n + BackwardFrames - prevFramesCount &&
                    frame < GetVideoInfo().num_frames;
                    frame++)
                    if (frame == n || OverlayStat[frame] == null)
                        OverlayStat[frame] = stabilizeFrames[frame - n];
                return info;
            }
            simple:
            return OverlayStat[n] = AutoOverlay(n);
        }

        private int Scale(int val, double coef) => (int) Math.Round(val * coef);

        private int Round(double val) => (int)Math.Round(val);

        private dynamic ResizeRotate(Clip clip, int width, int height, int angle = 0, RectangleF crop = default(RectangleF))
        {
            var resizeFunc = SrcInfo.Width * SrcInfo.Height < width * height ? Upsize : Downsize;
            return base.ResizeRotate(clip, resizeFunc, Rotate, width, height, angle, crop);
        }

        public OverlayConfig[] LoadConfigs()
        {
            return Configs == null
                ? new[] {new OverlayConfig()}
                : Enumerable.Range(0, Configs.GetVideoInfo().num_frames)
                    .Select(i => OverlayConfig.FromFrame(Configs.GetFrame(i, StaticEnv))).ToArray();
        }

        public OverlayInfo AutoOverlayImpl(int n, IEnumerable<OverlayConfig> configs = null)
        {
#if DEBUG
            extraWatch.Reset();
            diffWatch.Reset();
            totalWatch.Restart();
#endif
            configs = configs ?? LoadConfigs();
            var prevInfo = OverlayInfo.EMPTY;
            using (new VideoFrameCollector())
            using (new DynamicEnviroment(StaticEnv))
                foreach (var _config in configs)
                {
                    var config = _config;

                    double Coef(int step) => Math.Pow(config.ScaleBase, 1 - step);

                    int BaseResizeStep(int resizeStep) => resizeStep - Round(Math.Log(2, config.ScaleBase));

                    Clip StepResize(Clip clip, int resizeStep)
                    {
                        var coef = Coef(resizeStep);
                        var baseStep = BaseResizeStep(resizeStep);
                        var width = Scale(clip.GetVideoInfo().width, coef);
                        var height = Scale(clip.GetVideoInfo().height, coef);
                        var baseClip = baseStep <= 1 ? clip : StepResize(clip, baseStep);
                        return ResizeRotate(baseClip, width, height);
                    }
                    var ar1 = config.AspectRatio1 < double.Epsilon ? OverInfo.AspectRatio : config.AspectRatio1;
                    var ar2 = config.AspectRatio2 < double.Epsilon ? OverInfo.AspectRatio : config.AspectRatio2;
                    var maxAspectRatio = Math.Max(ar1, ar2);
                    var minAspectRatio = Math.Min(ar1, ar2);

                    var angle1 = Math.Min(config.Angle1 % 360, config.Angle2 % 360);
                    var angle2 = Math.Max(config.Angle1 % 360, config.Angle2 % 360);

                    var stepCount = 0;
                    for (;;stepCount++)
                    {
                        var testArea = Coef(stepCount + 1) * Coef(stepCount + 1) * SrcInfo.Area;
                        if (testArea < config.MinSampleArea)
                            break;
                        if (testArea < config.RequiredSampleArea && config.MaxSampleDiff < byte.MaxValue)
                        {
                            var baseStep = BaseResizeStep(stepCount + 1);
                            var baseClip = StepResize(Source, baseStep);
                            var testSize = new Size(baseClip.GetVideoInfo().width, baseClip.GetVideoInfo().height);
                            var testClip = ResizeRotate(StepResize(Source, stepCount + 1), testSize.Width, testSize.Height);
                            var test1 = baseClip.GetFrame(n, StaticEnv);
                            VideoFrame test2 = testClip[n];
                            var diff = FindBestIntersect(test1, null, testSize, test2, null, testSize, new Rectangle(0, 0, 1, 1), 0, 0).Diff;
                            if (diff > config.MaxSampleDiff)
                                break;
                        }
                    }

                    var bestAr = new List<OverlayInfo>();
                    for (var step = stepCount; step > 0; step--)
                    {
                        var initStep = !bestAr.Any();

                        if (initStep)
                            bestAr.Add(OverlayInfo.EMPTY);

                        var coefDiff = initStep ? 1 : Coef(step) / Coef(step + 1);
                        var coefCurrent = Coef(step);

                        int srcScaledWidth = Scale(SrcInfo.Width, coefCurrent), srcScaledHeight = Scale(SrcInfo.Height, coefCurrent);
                        var srcScaledArea = srcScaledWidth * srcScaledHeight;

                        var srcBase = StepResize(Source, step);
                        var srcMaskBase = SourceMask == null ? null : StepResize(SourceMask, step);
                        var overBase = StepResize(Overlay, step - 1);
                        var overMaskBase = OverlayMask == null ? null : StepResize(OverlayMask, step - 1);
                        
                        var defArea = Math.Min(SrcInfo.AspectRatio, OverInfo.AspectRatio) / Math.Max(SrcInfo.AspectRatio, OverInfo.AspectRatio) * 100;
                        if (config.MinSourceArea < double.Epsilon)
                            config.MinSourceArea = defArea;
                        if (config.MinOverlayArea < double.Epsilon)
                            config.MinOverlayArea = defArea;

                        var minIntersectArea = (int) (srcScaledArea * config.MinSourceArea / 100.0);
                        var maxOverlayArea = (int) (srcScaledArea / (config.MinOverlayArea / 100.0));

                        var testParams = new HashSet<TestOverlay>();
                        foreach (var best in bestAr)
                        {
                            var minWidth = Round(Math.Sqrt(minIntersectArea * minAspectRatio));
                            var maxWidth = Round(Math.Sqrt(maxOverlayArea * maxAspectRatio));

                            if (!initStep)
                            {
                                minWidth = Math.Max(minWidth, (int)((best.Width - config.Correction) * coefDiff));
                                maxWidth = Math.Min(maxWidth, Round((best.Width + config.Correction) * coefDiff) + 1);
                            }
                            
                            for (var width = minWidth; width <= maxWidth; width++)
                            {
                                var minHeight = Round(width / maxAspectRatio);
                                var maxHeight = Round(width / minAspectRatio);

                                if (!initStep)
                                {
                                    minHeight = Math.Max(minHeight, (int)((best.Height - config.Correction) * coefDiff));
                                    maxHeight = Math.Min(maxHeight, Round((best.Height + config.Correction) * coefDiff) + 1);
                                }
                                
                                for (var height = minHeight; height <= maxHeight; height++)
                                {
                                    var area = width * height;
                                    if (area < config.MinArea * coefCurrent * coefCurrent || area > Round(config.MaxArea * coefCurrent * coefCurrent))
                                        continue;

                                    var crop = RectangleF.Empty;

                                    if (config.FixedAspectRatio)
                                    {
                                        var cropWidth = (float) Math.Max(0, height * maxAspectRatio - width) / 2;
                                        cropWidth *= (float) overBase.GetVideoInfo().width / width;
                                        var cropHeight = (float) Math.Max(0, width / maxAspectRatio - height) / 2;
                                        cropHeight *= (float) overBase.GetVideoInfo().height / height;
                                        crop = RectangleF.FromLTRB(cropWidth, cropHeight, cropWidth, cropHeight);
                                    }

                                    Rectangle searchArea;
                                    if (initStep)
                                    {
                                        searchArea = new Rectangle(
                                            -width + 1,
                                            -height + 1,
                                            width + srcScaledWidth - 2,
                                            height + srcScaledHeight - 2
                                        );
                                    }
                                    else
                                    {
                                        var coefArea = (width * height * coefDiff) /
                                                        (best.Width * best.Height * coefDiff *
                                                        coefDiff);
                                        searchArea = new Rectangle(
                                            (int)((best.X - config.Correction) * coefArea),
                                            (int)((best.Y - config.Correction) * coefArea),
                                            Round(2 * coefArea * config.Correction) + 1,
                                            Round(2 * coefArea * config.Correction) + 1
                                        );
                                    }

                                    int oldMaxX = searchArea.Right - 1, oldMaxY = searchArea.Bottom - 1;
                                    searchArea.X = Math.Max(searchArea.X, (int) (config.MinX * coefCurrent));
                                    searchArea.Y = Math.Max(searchArea.Y, (int) (config.MinY * coefCurrent));
                                    searchArea.Width = Math.Min(oldMaxX - searchArea.X + 1, Round(config.MaxX * coefCurrent) - searchArea.X + 1);
                                    searchArea.Height = Math.Min(oldMaxY - searchArea.Y + 1, Round(config.MaxY * coefCurrent) - searchArea.Y + 1);

                                    int angleFrom = Round(angle1 * 100), angleTo = Round(angle2 * 100);

                                    if (!initStep)
                                    {
                                        angleFrom = FindNextAngle(2, best.Width, best.Height, best.Angle, angleFrom, false);
                                        angleTo = FindNextAngle(2, best.Width, best.Height, best.Angle, angleTo, true);
                                    }

                                    var size = Size.Empty;
                                    for (var angle = angleFrom; angle <= angleTo; angle++)
                                    {
                                        var newSize = BilinearRotate.CalculateSize(width, height, angle / 100.0);
                                        if (!size.Equals(newSize))
                                        {
                                            size = newSize;

                                            testParams.Add(new TestOverlay
                                            {
                                                Width = width,
                                                Height = height,
                                                Angle = size.Width == width && size.Height == height ? 0 : angle,
                                                SearchArea = searchArea,
                                                Crop = crop
                                            });
                                        }
                                    }
                                }
                            }
                        }

                        var results = PerformTest(testParams, n, 
                            srcBase, srcMaskBase, overBase, overMaskBase,
                            minIntersectArea, config.MinOverlayArea, config.Branches);

                        bestAr = results
                            .TakeWhile((p, i) => i < config.Branches && (p.Diff < float.Epsilon || p.Diff/results.First().Diff < 1 + config.BranchMaxDiff/100.0))
                            .Distinct()
                            .ToList();
                        bestAr.ForEach(best => Log($"Step: {step} X,Y: ({best.X},{best.Y}) Size: {best.Width}x{best.Height} ({best.AspectRatio:F2}:1) Angle: {best.Angle:F2} Diff: {best.Diff}"));
                        results.Clear();
                    }
                    var res = bestAr[0];
                    if (prevInfo.Diff < res.Diff)
                        res = prevInfo;
                    else prevInfo = res;
                    if (res.Diff <= config.AcceptableDiff || config == configs.Last())
                    { 

                        if (!config.FixedAspectRatio)
                        {
                            const double magic = 0.5;
                            minAspectRatio = res.Width > res.Height
                                ? (res.Width - magic) / res.Height
                                : res.Width / (res.Height + magic);
                            maxAspectRatio = res.Width > res.Height
                                ? (res.Width + magic) / res.Height
                                : res.Width / (res.Height - magic);
                        }
#if DEBUG
                        extraWatch.Start();
#endif
                        //config.PointCount = 1;
                        var subResults = new SortedSet<OverlayInfo>(bestAr);// { res };
                        int cropHorizontalShrinkWidth = OverlayInfo.CROP_VALUE_COUNT,
                            cropVerticalShrinkHeight = OverlayInfo.CROP_VALUE_COUNT, 
                            cropHorizontalFullWidth = 0,
                            cropVerticalFullHeight = 0;

                        
                        for (var substep = 1; substep <= config.Subpixel; substep++)
                        {
                            if (subResults.Min.Width == bestAr[0].Width - 1
                                && cropHorizontalShrinkWidth > subResults.Min.CropLeft + subResults.Min.CropRight)
                                cropHorizontalShrinkWidth = subResults.Min.CropLeft + subResults.Min.CropRight;
                            if (subResults.Min.Height == bestAr[0].Height - 1
                                && cropVerticalShrinkHeight > subResults.Min.CropTop + subResults.Min.CropBottom)
                                cropVerticalShrinkHeight = subResults.Min.CropTop + subResults.Min.CropBottom;
                            if (subResults.Min.Width == bestAr[0].Width
                                && cropHorizontalFullWidth < subResults.Min.CropLeft + subResults.Min.CropRight)
                                cropHorizontalFullWidth = subResults.Min.CropLeft + subResults.Min.CropRight;
                            if (subResults.Min.Height == bestAr[0].Height
                                && cropVerticalFullHeight < subResults.Min.CropTop + subResults.Min.CropBottom)
                                cropVerticalFullHeight = subResults.Min.CropTop + subResults.Min.CropBottom;
                            var bestCrops = subResults
                                .TakeWhile((p, i) => i < config.Branches && (p.Diff < float.Epsilon || p.Diff / subResults.First().Diff < 1 + config.BranchMaxDiff / 100.0))
                                .Distinct()
                                .ToArray();
                            var cropStep = (int) Math.Round(Math.Pow(2, -substep) * OverlayInfo.CROP_VALUE_COUNT);
                            if (cropStep == 0) break;
                            var testParams = new HashSet<TestOverlay>();
                            foreach (var bestCrop in bestCrops)
                            for (var cropLeft = bestCrop.CropLeft - cropStep;
                                cropLeft <= bestCrop.CropLeft + cropStep;
                                cropLeft += cropStep)
                            for (var cropTop = bestCrop.CropTop - cropStep;
                                cropTop <= bestCrop.CropTop + cropStep;
                                cropTop += cropStep)
                            for (var cropRight = bestCrop.CropRight - cropStep;
                                cropRight <= bestCrop.CropRight + cropStep;
                                cropRight += cropStep)
                            for (var cropBottom = bestCrop.CropBottom - cropStep;
                                cropBottom <= bestCrop.CropBottom + cropStep;
                                cropBottom += cropStep)
                            for (var width = bestAr[0].Width - 1; width <= bestAr[0].Width + 0; width++)
                            for (var height = bestAr[0].Height - 1; height <= bestAr[0].Height + 0; height++)
                            {
                                if (config.FixedAspectRatio)
                                {
                                    var orgWidth = OverInfo.Width - (cropLeft + cropRight) / OverlayInfo.CROP_VALUE_COUNT_R;
                                    var realWidth = (OverInfo.Width / orgWidth) * width;
                                    var realHeight = realWidth / maxAspectRatio;
                                    var orgHeight = OverInfo.Height / (realHeight / height);
                                    cropBottom = (int)((OverInfo.Height - orgHeight - cropTop / OverlayInfo.CROP_VALUE_COUNT_R)* OverlayInfo.CROP_VALUE_COUNT_R);
                                }
                                var subinfo = new OverlayInfo
                                {
                                    CropLeft = cropLeft,
                                    CropTop = cropTop,
                                    CropRight = cropRight,
                                    CropBottom = cropBottom
                                };
                                var actualWidth = width + (width / OverInfo.Width) * (cropLeft + cropRight) /
                                                    OverlayInfo.CROP_VALUE_COUNT_R;
                                var actualHeight = height + (height / OverInfo.Height) * (cropTop + cropBottom) /
                                                    OverlayInfo.CROP_VALUE_COUNT_R;
                                var actualAspectRatio = actualWidth / actualHeight;

                                var test = new TestOverlay
                                {
                                    Width = width,
                                    Height = height,
                                    Angle = bestCrop.Angle,
                                    Crop = subinfo.GetCrop(),
                                    SearchArea = new Rectangle(bestAr[0].X - 0, bestAr[0].Y - 0, 2, 2)
                                };

                                var ignore = cropLeft < 0 || cropTop < 0 || cropRight < 0 || cropBottom < 0
                                    || width == bestAr[0].Width && cropLeft + cropRight > cropHorizontalShrinkWidth
                                    || height == bestAr[0].Height && cropTop + cropBottom > cropVerticalShrinkHeight
                                    || width == bestAr[0].Width - 1 && cropLeft + cropRight < cropHorizontalFullWidth
                                    || height == bestAr[0].Height - 1 && cropTop + cropBottom < cropVerticalFullHeight
                                    || cropLeft + cropRight > OverlayInfo.CROP_VALUE_COUNT
                                    || cropTop + cropBottom > OverlayInfo.CROP_VALUE_COUNT
                                    || cropLeft >= OverlayInfo.CROP_VALUE_COUNT
                                    || cropTop >= OverlayInfo.CROP_VALUE_COUNT
                                    || cropRight >= OverlayInfo.CROP_VALUE_COUNT
                                    || cropBottom >= OverlayInfo.CROP_VALUE_COUNT
                                    || (cropLeft == 0 && cropTop == 0 && cropRight == 0 && cropBottom == 0)
                                    || (!config.FixedAspectRatio && actualAspectRatio <= minAspectRatio)
                                    || (!config.FixedAspectRatio && actualAspectRatio >= maxAspectRatio);

                                if (config.FixedAspectRatio)
                                    cropBottom = short.MaxValue;

                                if (!ignore)
                                    testParams.Add(test);
                            }
                            subResults.UnionWith(PerformTest(testParams, n,
                                Source, SourceMask, Overlay, OverlayMask, 0, 0, config.Branches));
                        }
                        if (subResults.Any())
                            res = subResults.Min;
#if DEBUG
                        extraWatch.Stop();
                        totalWatch.Stop();
                        Log(
                            $"Total: {totalWatch.ElapsedMilliseconds} ms. " +
                            $"Subpixel: {extraWatch.ElapsedMilliseconds} ms. " +
                            $"Diff: {diffWatch.ElapsedMilliseconds} ms. Step count: {stepCount}");
#endif
                        res.FrameNumber = n;
                        return res;
                    }
                }
            throw new InvalidOperationException();
        }

        private struct TestOverlay
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public RectangleF Crop { get; set; }
            public int Angle { get; set; }
            public Rectangle SearchArea { get; set; }

            public bool Equals(TestOverlay other)
            {
                return Width == other.Width && Height == other.Height && Crop.Equals(other.Crop) && Angle == other.Angle && SearchArea.Equals(other.SearchArea);
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                return obj is TestOverlay && Equals((TestOverlay) obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = Width;
                    hashCode = (hashCode * 397) ^ Height;
                    hashCode = (hashCode * 397) ^ Crop.GetHashCode();
                    hashCode = (hashCode * 397) ^ Angle;
                    hashCode = (hashCode * 397) ^ SearchArea.GetHashCode();
                    return hashCode;
                }
            }
        }

        private OverlayInfo RepeatImpl(OverlayInfo testInfo, int n)
        {
            using (new VideoFrameCollector())
            using (new DynamicEnviroment(StaticEnv))
            {
                var src = Source.GetFrame(n, StaticEnv);
                var srcMask = SourceMask?.GetFrame(n, StaticEnv);
                var overMaskClip = this.OverlayMask;
                if (overMaskClip == null && testInfo.Angle != 0)
                    overMaskClip = GetBlankClip(Overlay, true);
                VideoFrame overMask = overMaskClip == null
                    ? null
                    : ResizeRotate(overMaskClip, testInfo.Width, testInfo.Height, testInfo.Angle)[n];
                VideoFrame over = ResizeRotate(Overlay, testInfo.Width, testInfo.Height, testInfo.Angle,
                    testInfo.GetCrop())[n];
                var searchArea = new Rectangle(testInfo.X, testInfo.Y, 1, 1);
                var info = FindBestIntersect(
                    src, srcMask, new Size(SrcInfo.Width, SrcInfo.Height),
                    over, overMask, new Size(testInfo.Width, testInfo.Height),
                    searchArea, 0, 0);
                info.Angle = testInfo.Angle;
                info.Width = testInfo.Width;
                info.Height = testInfo.Height;
                info.SetCrop(testInfo.GetCrop());
                return info;
            }
        }
        
        private ISet<OverlayInfo> PerformTest(
            IEnumerable<TestOverlay> testParams, 
            int n, Clip srcBase, Clip srcMaskBase, Clip overBase, Clip overMaskBase, 
            int minIntersectArea, double minOverlayArea, int branches)
        {
            var results = new SortedSet<OverlayInfo>();
            var tasks = from test in testParams
                let transform = new {test.Width, test.Height, test.Crop, test.Angle}
                group test.SearchArea by transform
                into testGroup
                let src = srcBase.GetFrame(n, StaticEnv)
                let srcMask = srcMaskBase?.GetFrame(n, StaticEnv)
                let srcSize = new Size(srcBase.GetVideoInfo().width, srcBase.GetVideoInfo().height)
                let overClip = (Clip) ResizeRotate(overBase, testGroup.Key.Width, testGroup.Key.Height, testGroup.Key.Angle, testGroup.Key.Crop)
                let over = overClip.GetFrame(n, StaticEnv)
                let alwaysNullMask = overMaskBase == null && testGroup.Key.Angle == 0
                let rotationMask = overMaskBase == null && testGroup.Key.Angle != 0
                let overMask = (VideoFrame) (alwaysNullMask
                    ? null
                    : ResizeRotate(rotationMask
                        ? GetBlankClip(overBase, true)
                        : overMaskBase, testGroup.Key.Width, testGroup.Key.Height, testGroup.Key.Angle)[n])
                let overSize = new Size(overClip.GetVideoInfo().width, overClip.GetVideoInfo().height)
                select new {src, srcMask, srcSize, over, overMask, overSize, testGroup};

            Task.WaitAll(tasks.Select(task => Task.Factory.StartNew(() => Parallel.ForEach(task.testGroup,
                searchArea =>
                {
                    double reference;
                    lock (results)
                        reference = results.TakeWhile((p, i) => i < branches)
                                        .Select((p, i) => new {p.Diff, i})
                                        .FirstOrDefault(p => p.i == branches - 1)
                                        ?.Diff ?? double.MaxValue;
#if DEBUG
                    diffWatch.Start();
#endif
                    var stat = FindBestIntersect(
                        task.src, task.srcMask, task.srcSize,
                        task.over, task.overMask, task.overSize,
                        searchArea, minIntersectArea, minOverlayArea);
#if DEBUG
                    diffWatch.Stop();
#endif
                    stat.Angle = task.testGroup.Key.Angle;
                    stat.Width = task.testGroup.Key.Width;
                    stat.Height = task.testGroup.Key.Height;
                    stat.SetCrop(task.testGroup.Key.Crop);
                    if (stat.Diff < reference)
                        lock(results)
                            results.Add(stat);
                }))).Cast<Task>().ToArray());
            return results;
        }

        private OverlayInfo FindBestIntersect(
            VideoFrame src, VideoFrame srcMask, Size srcSize, 
            VideoFrame over, VideoFrame overMask, Size overSize, 
            Rectangle searchArea, int minIntersectArea, double minOverlayArea)
        {
            var pixelSize = src.GetRowSize() / srcSize.Width;
            var srcData = src.GetReadPtr();
            var srcStride = src.GetPitch();
            var overStride = over.GetPitch();
            var overData = over.GetReadPtr();
            var srcMaskData = srcMask?.GetReadPtr() ?? IntPtr.Zero;
            var srcMaskStride = srcMask?.GetPitch() ?? 0;
            var overMaskStride = overMask?.GetPitch() ?? 0;
            var overMaskData = overMask?.GetReadPtr() ?? IntPtr.Zero;
            var depth = SrcInfo.ColorSpace.GetBitDepth();

            var best = new OverlayInfo
            {
                Diff = double.MaxValue,
                Width = overSize.Width,
                Height = overSize.Height
            };

            var searchPoints = Enumerable.Range(searchArea.X, searchArea.Width).SelectMany(x =>
                Enumerable.Range(searchArea.Y, searchArea.Height).Select(y => new Point(x, y)));

            unsafe
            {
                Parallel.ForEach(searchPoints, testPoint =>
                {
                    var sampleHeight = Math.Min(overSize.Height - Math.Max(0, -testPoint.Y), srcSize.Height - Math.Max(0, testPoint.Y));
                    var srcOffset = srcData + Math.Max(0, testPoint.Y) * srcStride;
                    var overOffset = overData + Math.Max(0, -testPoint.Y) * overStride;
                    var srcMaskOffset = srcMaskData + Math.Max(0, testPoint.Y) * srcMaskStride;
                    var overMaskOffset = overMaskData + Math.Max(0, -testPoint.Y) * overMaskStride;
                    var sampleWidth = Math.Min(overSize.Width - Math.Max(0, -testPoint.X),
                        srcSize.Width - Math.Max(0, testPoint.X));
                    double sampleArea = sampleWidth * sampleHeight;
                    if (sampleArea < minIntersectArea || (sampleArea/(overSize.Width*overSize.Height) < (minOverlayArea/100.0)))
                        return;
                    var srcRow = srcOffset + Math.Max(0, testPoint.X) * pixelSize;
                    var overRow = overOffset + Math.Max(0, -testPoint.X) * pixelSize;
                    var srcMaskRow = srcMaskOffset + Math.Max(0, testPoint.X) * pixelSize;
                    var overMaskRow = overMaskOffset + Math.Max(0, -testPoint.X) * pixelSize;
                    var rmse = Math.Sqrt(NativeUtils.SquaredDifferenceSum(
                                srcRow, srcStride,
                                srcMask == null ? IntPtr.Zero : srcMaskRow, srcMaskStride,
                                overRow, overStride,
                                overMask == null ? IntPtr.Zero : overMaskRow, overMaskStride,
                                sampleWidth*pixelSize, sampleHeight, depth));
                    lock (best)
                        if (rmse < best.Diff)
                        {
                            best.Diff = rmse;
                            best.X = testPoint.X;
                            best.Y = testPoint.Y;
                        }
                });
            }
            return best;
        }

        private static int FindNextAngle(int n, int width, int height, int baseAngle, int max, bool forward)
        {
            var tmpSize = BilinearRotate.CalculateSize(width, height, baseAngle / 100.0);
            var increment = forward ? 1 : -1;
            var check = forward ? (Func<int, bool>)(angle => angle <= max) : (angle => angle >= max);
            for (var angle = baseAngle; check(angle); angle += increment)
            {
                var newSize = BilinearRotate.CalculateSize(width, height, angle / 100.0);
                if (!tmpSize.Equals(newSize))
                {
                    if (--n == 0)
                        return angle;
                    tmpSize = newSize;
                }
            }
            return max;
        }

        protected sealed override void Dispose(bool A_0)
        {
            form?.Close();
            base.Dispose(A_0);
        }
    }
}
