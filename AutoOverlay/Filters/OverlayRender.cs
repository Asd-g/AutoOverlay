using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AvsFilterNet;

namespace AutoOverlay
{
    public abstract class OverlayRender : OverlayFilter
    {
        public abstract Clip Source { get; protected set; }
        public abstract Clip Overlay { get; protected set; }
        public abstract Clip SourceMask { get; protected set; }
        public abstract Clip OverlayMask { get; protected set; }
        public abstract string OverlayMode { get; protected set; }
        public abstract string AdjustChannels { get; protected set; }
        public abstract int Width { get; protected set; }
        public abstract int Height { get; protected set; }
        public abstract string PixelType { get; protected set; }
        public abstract int Gradient { get; protected set; }
        public abstract int Noise { get; protected set; }
        public abstract bool DynamicNoise { get; protected set; }
        public abstract FramingMode Mode { get; protected set; }
        public abstract double Opacity { get; protected set; }
        public abstract string Upsize { get; protected set; }
        public abstract string Downsize { get; protected set; }
        public abstract string Rotate { get; protected set; }
        public abstract double ColorAdjust { get; protected set; }
        public abstract string Matrix { get; protected set; }
        public abstract bool Invert { get; protected set; }
        public abstract bool Extrapolation { get; protected set; }
        public abstract bool SIMD { get; protected set; }

        private YUVPlanes[] planes;

        private Size srcSubsample;
        private Size overSubsample;
        private Size targetSubsample;

        private static readonly Size NO_SUBSAMPLE = new Size(1, 1);

        protected override void Initialize(AVSValue args)
        {
            base.Initialize(args);
            var srcInfo = Source.GetVideoInfo();
            var overInfo = Overlay.GetVideoInfo();
            var vi = (Invert ? Overlay : Source).GetVideoInfo();
            if (Width > 0)
                vi.width = Width;
            if (Height > 0)
                vi.height = Height;
            var srcBitDepth = Source.GetVideoInfo().pixel_type.GetBitDepth();
            var overBitDepth = Overlay.GetVideoInfo().pixel_type.GetBitDepth();
            if (srcBitDepth != overBitDepth && ColorAdjust > 0 && ColorAdjust < 1)
                throw new AvisynthException("ColorAdjust -1, 0, 1 only allowed when video bit depth is different");
            vi.pixel_type = vi.pixel_type.ChangeBitDepth(ColorAdjust > 1 - double.Epsilon ? overBitDepth : srcBitDepth);
            vi.num_frames = Child.GetVideoInfo().num_frames;
            planes = OverlayUtils.GetPlanes(vi.pixel_type);
            srcSubsample = new Size(srcInfo.pixel_type.GetWidthSubsample(), srcInfo.pixel_type.GetHeightSubsample());
            overSubsample = new Size(overInfo.pixel_type.GetWidthSubsample(), overInfo.pixel_type.GetHeightSubsample());
            targetSubsample = Invert ? overSubsample : srcSubsample;
            if (Invert && ColorAdjust >= 0)
                ColorAdjust = 1 - ColorAdjust;
            SetVideoInfo(ref vi);
        }

        protected override VideoFrame GetFrame(int n)
        {
            Size srcSize, overSize, targetSize;
            Clip src, over, srcMask, overMask;

            var info = GetOverlayInfo(n).Resize(Source.GetSize(), Overlay.GetSize());
            targetSize = GetVideoInfo().GetSize();
            if (Invert)
            {
                info = info.Invert();
                srcSize = Overlay.GetSize();
                overSize = Source.GetSize();
                src = Overlay;
                over = Source;
                srcMask = OverlayMask;
                overMask = SourceMask;
            }
            else
            {
                srcSize = Source.GetSize();
                overSize = Overlay.GetSize();
                src = Source;
                over = Overlay;
                overMask = OverlayMask;
                srcMask = SourceMask;
                //info = info.Invert(Source.GetSize(), Overlay.GetSize()).Invert(Overlay.GetSize(), Source.GetSize());
            }

            var res = NewVideoFrame(StaticEnv);
            dynamic hybrid;

       
            if (srcSubsample == NO_SUBSAMPLE && overSubsample == NO_SUBSAMPLE || !GetVideoInfo().pixel_type.IsRealPlanar())
            {
                hybrid = RenderFrame(info, src, over, srcMask, overMask, targetSize);
            }
            else
            {
                var chromaSrcSize = new Size(srcSize.Width / srcSubsample.Width, srcSize.Height / srcSubsample.Height);
                var chromaOverSize = new Size(overSize.Width / overSubsample.Width, overSize.Height / overSubsample.Height);
                var chromaSize = new Size(targetSize.Width / targetSubsample.Width, targetSize.Height / targetSubsample.Height);
                var chromaInfo = info.Resize(chromaSrcSize, chromaOverSize);
                srcMask = srcMask?.IsRealPlanar() ?? false ? srcMask?.Dynamic()?.ExtractY() : srcMask;
                overMask = overMask?.IsRealPlanar() ?? false ? overMask?.Dynamic()?.ExtractY() : overMask;
                var chromaSrcMask = chromaSize == targetSize ? srcMask : srcMask?.Dynamic()?.PointResize(chromaSrcSize.Width, chromaSrcSize.Height);
                var chromaOverMask = chromaSize == targetSize ? overMask : overMask?.Dynamic()?.PointResize(chromaOverSize.Width, chromaOverSize.Height);
                var luma = RenderFrame(info, DynamicEnv.ExtractY(src), DynamicEnv.ExtractY(over), srcMask, overMask, targetSize);
                var chromaU = RenderFrame(chromaInfo, DynamicEnv.ExtractU(src), DynamicEnv.ExtractU(over), chromaSrcMask, chromaOverMask, chromaSize, true);
                var chromaV = RenderFrame(chromaInfo, DynamicEnv.ExtractV(src), DynamicEnv.ExtractV(over), chromaSrcMask, chromaOverMask, chromaSize, true);
                Log($"Luma: {((Clip)luma).GetSize()}");
                Log($"Chroma U: {((Clip)chromaU).GetSize()}");
                Log($"Chroma V: {((Clip)chromaV).GetSize()}");
                hybrid = DynamicEnv.CombinePlanes(luma, chromaU, chromaV, planes: "YUV",
                    pixel_type:
                    $"YUV4{4 / targetSubsample.Width}{targetSubsample.Width - 4 / targetSubsample.Height}P{GetVideoInfo().pixel_type.GetBitDepth()}");
            }
            if (Debug)
                hybrid = hybrid.Subtitle(info.ToString(overSize).Replace("\n", "\\n"), lsp: 0);
            using (VideoFrame frame = hybrid[info.FrameNumber])
            {
                Parallel.ForEach(planes, plane =>
                {
                    for (var y = 0; y < frame.GetHeight(plane); y++)
                        OverlayUtils.CopyMemory(res.GetWritePtr(plane) + y * res.GetPitch(plane),
                            frame.GetReadPtr(plane) + y * frame.GetPitch(plane), res.GetRowSize(plane));
                });
            }
            return res;
        }

        protected abstract OverlayInfo GetOverlayInfo(int n);

        protected dynamic RenderFrame(OverlayInfo info, 
            Clip source, Clip overlay,
            Clip sourceMask, Clip overlayMask,
            Size targetSize, bool chroma = false)
        {
            var src = source.Dynamic();
            var over = overlay.Dynamic();
            var srcSize = source.GetSize();
            var overSize = overlay.GetSize();

            if (Mode == FramingMode.Fit && srcSize != targetSize)
            {
                info = info.Resize(targetSize, overSize);
                src = src.Invoke(srcSize.GetArea() < targetSize.GetArea() ? Upsize : Downsize, targetSize.Width, targetSize.Height);
                srcSize = targetSize;
            }
            if (Mode == FramingMode.Fit)
            {
                info = info.Shrink(srcSize, overSize);
            }

            var resizeFunc = info.Width > overSize.Width ? Upsize : Downsize;

        
            var crop = info.GetCrop();

            if (!crop.IsEmpty || info.Width != overSize.Width || info.Height != overSize.Height)
            {
                over = ResizeRotate(over, resizeFunc, null, info.Width, info.Height, 0, crop);
            }
            var overMask = overlayMask?.Dynamic().BicubicResize(info.Width, info.Height);

            var mergedWidth = srcSize.Width + Math.Max(-info.X, 0) + Math.Max(info.Width + info.X - srcSize.Width, 0);
            var mergedHeight = srcSize.Height + Math.Max(-info.Y, 0) + Math.Max(info.Height + info.Y - srcSize.Height, 0);
            var mergedAr = mergedWidth / (double) mergedHeight;
            var outAr = targetSize.Width / (double) targetSize.Height;
            var wider = mergedAr > outAr;
            if (Mode == FramingMode.FitBorders)
                wider = !wider;
            var finalWidth = wider ? targetSize.Width : (int)Math.Round(targetSize.Width * (mergedAr / outAr));
            var finalHeight = wider ? (int)Math.Round(targetSize.Height * (outAr / mergedAr)) : targetSize.Height;
            var finalX = wider ? 0 : (targetSize.Width - finalWidth) / 2;
            var finalY = wider ? (targetSize.Height - finalHeight) / 2 : 0;

            if (ColorAdjust > -double.Epsilon)
            {
                var srcTest = src.Crop(Math.Max(0, info.X), Math.Max(0, info.Y),
                    -Math.Max(0, srcSize.Width - info.X - info.Width),
                    -Math.Max(0, srcSize.Height - info.Y - info.Height));
                var overTest = over.Crop(Math.Max(0, -info.X), Math.Max(0, -info.Y),
                    -Math.Max(0, -(srcSize.Width - info.X - info.Width)),
                    -Math.Max(0, -(srcSize.Height - info.Y - info.Height)));
                var maskTest = sourceMask?.Dynamic().Crop(Math.Max(0, info.X), Math.Max(0, info.Y),
                    -Math.Max(0, srcSize.Width - info.X - info.Width),
                    -Math.Max(0, srcSize.Height - info.Y - info.Height));
                if (overMask != null)
                    maskTest = (maskTest ?? GetBlankClip(src, true).Dynamic())
                        .Overlay(overMask, info.X, info.Y, mode: "darken")
                        .Crop(Math.Max(0, -info.X), Math.Max(0, -info.Y),
                            -Math.Max(0, -(srcSize.Width - info.X - info.Width)),
                            -Math.Max(0, -(srcSize.Height - info.Y - info.Height)));
                if (!GetVideoInfo().IsRGB() && !string.IsNullOrEmpty(Matrix))
                {
                    srcTest = srcTest.ConvertToRgb24(matrix: Matrix);
                    overTest = overTest.ConvertToRgb24(matrix: Matrix);
                    maskTest = maskTest?.ConvertToRgb24(matrix: Matrix);
                    if (ColorAdjust > double.Epsilon)
                        src = src.ConvertToRgb24(matrix: Matrix);
                    if (ColorAdjust < 1 - double.Epsilon)
                        over = over.ConvertToRgb24(matrix: Matrix);
                }
                if (ColorAdjust > double.Epsilon)
                {
                    src = src.ColorAdjust(srcTest, overTest, maskTest, maskTest, intensity: ColorAdjust, channels: AdjustChannels, debug: Debug, SIMD: SIMD);
                    if (!GetVideoInfo().IsRGB() && !string.IsNullOrEmpty(Matrix))
                        src = src.ConvertToYV24(matrix: Matrix);
                }
                if (ColorAdjust < 1 - double.Epsilon)
                {
                    over = over.ColorAdjust(overTest, srcTest, maskTest, maskTest, intensity: 1 - ColorAdjust, channels: AdjustChannels, debug: Debug, exclude: 0, SIMD: SIMD);
                    if (!GetVideoInfo().IsRGB() && !string.IsNullOrEmpty(Matrix))
                        over = over.ConvertToYV24(matrix: Matrix);
                }
            }

            dynamic GetOverMask(int length, bool gradientMask, bool noiseMask)
            {
                return over.OverlayMask(
                    left: info.X > 0 ? length : 0,
                    top: info.Y > 0 ? length : 0,
                    right: srcSize.Width - info.X - info.Width > 0 ? length : 0,
                    bottom: srcSize.Height - info.Y - info.Height > 0 ? length : 0,
                    gradient: gradientMask, noise: noiseMask,
                    seed: DynamicNoise ? info.FrameNumber : 0);
            }

            dynamic GetSourceMask(int length, bool gradientMask, bool noiseMask)
            {
                return src.OverlayMask(
                    left: info.X < 0 ? length : 0,
                    top: info.Y < 0 ? length : 0,
                    right: srcSize.Width - info.X - info.Width < 0 ? length : 0,
                    bottom: srcSize.Height - info.Y - info.Height < 0 ? length : 0,
                    gradient: gradientMask, noise: noiseMask,
                    seed: DynamicNoise ? info.FrameNumber : 0);
            }

            dynamic GetMask(Func<int,bool,bool,dynamic> func)
            {
                if (Gradient > 0 && Gradient == Noise)
                    return func(Gradient, true, true);
                dynamic mask = null;
                if (Gradient > 0)
                    mask = func(Gradient, true, false);
                if (Noise > 0)
                {
                    var noiseMask = func(Noise, false, true);
                    mask = mask == null ? noiseMask : mask.Overlay(noiseMask, mode: "darken");
                }
                return mask;
            }

            dynamic Rotate(dynamic clip, bool invert) => clip == null
                ? null
                : (info.Angle == 0 ? clip : clip.Invoke(this.Rotate, (invert ? -info.Angle : info.Angle) / 100.0));

            switch (Mode)
            {
                case FramingMode.Fit:
                {
                    var mask = GetMask(GetOverMask);
                    if (overMask != null && mask != null)
                        mask = mask.Overlay(overMask, mode: "darken");
                    if (sourceMask != null && mask != null)
                        mask = mask.Overlay(Rotate(sourceMask.Dynamic().Invert(), true), -info.X, -info.Y, mode: "lighten");
                    if (mask == null && info.Angle != 0)
                        mask = ((Clip)GetBlankClip(over, true)).Dynamic();
                    return Opacity < double.Epsilon ? src : src.Overlay(Rotate(over, false), info.X, info.Y, mask: Rotate(mask, false), opacity: Opacity, mode: OverlayMode);
                }
                case FramingMode.Fill:
                {
                    var maskOver = GetMask(GetOverMask);
                    var maskSrc = GetMask(GetSourceMask);
                    if (overMask != null && maskOver != null)
                        maskOver = maskOver.Overlay(overMask, mode: "darken");
                    if (sourceMask != null && maskOver != null)
                        maskOver = maskOver.Overlay(sourceMask.Dynamic().Invert().Invoke(this.Rotate, -info.Angle / 100.0), -info.X, -info.Y, mode: "lighten");
                    dynamic hybrid = GetVideoInfo().IsRGB()
                        ? src.BlankClip(width: mergedWidth, height: mergedHeight)
                        : src.BlankClip(width: mergedWidth, height: mergedHeight, color_yuv: chroma ? 128 << 16 : 0);
                    if (Opacity - 1 <= -double.Epsilon)
                        hybrid = hybrid.Overlay(src, Math.Max(0, -info.X), Math.Max(0, -info.Y));
                    else maskSrc = null;
                    if (maskOver != null || Opacity - 1 < double.Epsilon)
                        hybrid = hybrid.Overlay(over.Invoke(this.Rotate, info.Angle / 100.0), Math.Max(0, info.X), Math.Max(0, info.Y));
                    if (maskOver == null && info.Angle != 0)
                        maskOver = GetBlankClip(over, true).Dynamic();

                    var merged = hybrid.Overlay(src, Math.Max(0, -info.X), Math.Max(0, -info.Y), mask: maskSrc)
                        .Overlay(
                            over.Invoke(this.Rotate, info.Angle / 100.0),
                            x: Math.Max(0, info.X),
                            y: Math.Max(0, info.Y),
                            opacity: Opacity,
                            mask: maskOver?.Invoke(this.Rotate, info.Angle / 100.0),
                            mode: OverlayMode);

                    /*var inpaintMask = src.BlankClip(width: mergedWidth, height: mergedHeight, color: 0x00FFFFFF, pixel_type: "RGB32")
                        .Overlay(src.BlankClip(color: 0, pixel_type: "RGB32")
                            .Crop(Gradient, Gradient, -Gradient, -Gradient)
                            .AddBorders(Gradient, Gradient, Gradient, Gradient, color: 0x00FFFFFF), 
                            Math.Max(0, -info.X), Math.Max(0, -info.Y))
                        .Overlay(over.BlankClip(color: 0, pixel_type: "RGB32")
                            .Crop(Gradient, Gradient, -Gradient, -Gradient)
                            .AddBorders(Gradient, Gradient, Gradient, Gradient, color: 0x00FFFFFF)
                            .Invoke(this.Rotate, info.Angle / 100.0), Math.Max(0, info.X), Math.Max(0, info.Y), mode: "darken");
                    inpaintMask = inpaintMask.Trim(info.FrameNumber - 0, info.FrameNumber + 0);

                    var tr = 2;

                    merged = merged.Trim(info.FrameNumber - tr, info.FrameNumber + tr);

                    var ppmode = 1;
                    var pp = 100;
                    var radius = 12;
                    var preblur = 12;
                    
                    if (info.X < 0 && info.Y > 0)
                        merged = merged.InpaintFunc(inpaintMask, loc: "TL", mode: "inpaint", radius: radius, preblur: preblur, speed: 20, reset: true, pp: pp, ppmode: ppmode);
                    if (info.X + info.Width > srcSize.Width && info.Y > 0)
                        merged = merged.InpaintFunc(inpaintMask, loc: "TR", mode: "inpaint", radius: radius, preblur: preblur, speed: 20, reset: true, pp: pp, ppmode: ppmode);
                    if (info.X < 0 && info.Y + info.Height < srcSize.Height)
                        merged = merged.InpaintFunc(inpaintMask, loc: "BL", mode: "inpaint", radius: radius, preblur: preblur, speed: 20, reset: true, pp: pp, ppmode: ppmode);
                    if (info.X + info.Width > srcSize.Width && info.Y + info.Height < srcSize.Height)
                        merged = merged.InpaintFunc(inpaintMask, loc: "BR", mode: "inpaint", radius: radius, preblur: preblur, speed: 20, reset: true, pp: pp, ppmode: ppmode);

                    merged = merged.Overlay(merged.AddGrain(var: 2, uvar: 1, sse2: true), mask: inpaintMask);

                    merged = merged.Trim(tr, tr);

                    merged = merged
                        .Overlay(src.Trim(info.FrameNumber, info.FrameNumber), Math.Max(0, -info.X), Math.Max(0, -info.Y), mask: GetMask(GetSourceMask).Trim(info.FrameNumber, info.FrameNumber))
                        .Overlay(
                            over.Trim(info.FrameNumber, info.FrameNumber).Invoke(this.Rotate, info.Angle / 100.0),
                            x: Math.Max(0, info.X),
                            y: Math.Max(0, info.Y),
                            opacity: Opacity,
                            mask: maskOver?.Invoke(this.Rotate, info.Angle / 100.0)?.Trim(info.FrameNumber, info.FrameNumber),
                            mode: OverlayMode);*/

                        /*var logo = merged.Trim(info.FrameNumber - 1, info.FrameNumber + 1).ConvertToRGB32();
                        logo=logo.AnalyzeLogo(inpaintMask.Trim(info.FrameNumber - 1, info.FrameNumber + 1)).ConvertToRGB32();
                        Clip logoClip = logo;
                        logo = logo.Crop(0, 0, 0, logoClip.GetVideoInfo().height / 2).Mask(logo.Crop(0, logoClip.GetVideoInfo().height / 2, 0, 0));

                        var FullClip = merged.ConvertToRGB32();

                        merged = merged.ConvertToRGB24();

                        merged = merged.DeblendLogo(logo.ConvertToRGB24(), logo);
                        merged = merged.InpaintLogo(logo.Mask(logo.ShowAlpha().Levels(235, 1, 236, 0, 255)), 4.0, 50.0, 4.0, 6.0);
                        merged = FullClip.Layer(merged.ConvertToRGB32().Mask(inpaintMask.DistanceFunction(256.0 / 8.0))).ConvertToY8();*/

                        var resized = merged.Invoke(Downsize, finalWidth, finalHeight);

                        var blankClip = GetVideoInfo().IsRGB()
                            ? src.BlankClip(width: targetSize.Width, height: targetSize.Height)
                            : src.BlankClip(width: targetSize.Width, height: targetSize.Height, color_yuv: chroma ? 128 << 16 : 0);

                    return blankClip.Overlay(resized, finalX, finalY);
                }
                case FramingMode.FillRectangle:
                case FramingMode.FitBorders:
                {
                    var maskOver = GetMask(GetOverMask);
                    var maskSrc = GetMask(GetSourceMask);
                    if (overMask != null && maskOver != null)
                        maskOver = maskOver.Overlay(overMask, mode: "darken");
                    if (sourceMask != null && maskOver != null)
                        maskOver = maskOver.Overlay(sourceMask.Dynamic().Invert().Invoke(this.Rotate, -info.Angle / 100.0), -info.X, -info.Y, mode: "lighten");
                    var background = src.BilinearResize(mergedWidth / 3, mergedHeight / 3).Overlay(over.BilinearResize(mergedWidth / 3, mergedHeight / 3),
                        opacity: 0.5, mask: overMask?.BilinearResize(mergedWidth / 3, mergedHeight / 3)); //TODO !!!!!!!!!!
                    for (var i = 0; i < 15; i++)
                        background = background.Blur(1.5);
                    background = background.GaussResize(mergedWidth, mergedHeight, p: 3);
                    //var background = src.BlankClip(width: mergedWidth, height: mergedHeight)
                    //        .Overlay(src, Math.Max(0, -info.X), Math.Max(0, -info.Y))
                    //        .Overlay(over.Invoke(rotateFunc, info.Angle / 100.0), Math.Max(0, info.X), Math.Max(0, info.Y));
                    if (Opacity - 1 <= -double.Epsilon)
                        background = background.Overlay(over.Invoke(this.Rotate, info.Angle / 100.0), Math.Max(0, info.X), Math.Max(0, info.Y), mask: maskOver?.Invoke(this.Rotate, info.Angle / 100.0));
                    var hybrid = background.Overlay(src, Math.Max(0, -info.X), Math.Max(0, -info.Y), mask: maskSrc)
                        .Overlay(over.Invoke(this.Rotate, info.Angle / 100.0), 
                            x: Math.Max(0, info.X), 
                            y: Math.Max(0, info.Y), 
                            mask: maskOver?.Invoke(this.Rotate, info.Angle / 100.0), 
                            mode: OverlayMode,
                            opacity:Opacity)
                        .Invoke(Downsize, finalWidth, finalHeight);
                    if (Mode == FramingMode.FillRectangle)
                        return src.BlankClip(width: targetSize.Width, height: targetSize.Height,
                                color_yuv: chroma ? 128 << 16 : 0)
                            .Overlay(hybrid, finalX, finalY);
                    var srcRect = new Rectangle(0, 0, srcSize.Width, srcSize.Height);
                    var overRect = new Rectangle(info.X, info.Y, info.Width, info.Height);
                    var union = Rectangle.Union(srcRect, overRect);
                    var intersect = Rectangle.Intersect(srcRect, overRect);
                    var cropLeft =  intersect.Left - union.Left;
                    var cropTop = intersect.Top - union.Top;
                    var cropRight = union.Right - intersect.Right;
                    var cropBottom = union.Bottom - intersect.Bottom;
                    var cropWidthCoef = cropRight == 0 ? 1 : ((double) cropLeft / cropRight) / ((double) cropLeft / cropRight + 1);
                    var cropHeightCoef = cropBottom == 0 ? 1 : ((double)cropTop / cropBottom) / ((double)cropTop / cropBottom + 1);
                    cropLeft = (int) ((finalWidth - targetSize.Width) * cropWidthCoef);
                    cropTop = (int)((finalHeight - targetSize.Height) * cropHeightCoef);
                    cropRight = finalWidth - targetSize.Width - cropLeft;
                    cropBottom = finalHeight - targetSize.Height - cropTop;
                    return hybrid.Crop(cropLeft, cropTop, -cropRight, -cropBottom);
                }
                case FramingMode.FillFull:
                {
                    finalWidth = wider ? mergedWidth : (int) Math.Round(mergedHeight * outAr);
                    finalHeight = wider ? (int) Math.Round(mergedWidth / outAr) : mergedHeight;
                    finalX = (finalWidth - mergedWidth) / 2;
                    finalY = (finalHeight - mergedHeight) / 2;
                    var maskOver = GetMask(GetOverMask);
                    var maskSrc = GetMask(GetSourceMask);
                    if (maskSrc != null && maskOver != null)
                        maskOver = maskOver.Overlay(maskSrc.Invert().Invoke(this.Rotate, -info.Angle / 100.0), -info.X, -info.Y, mode: "lighten");
                    if (overMask != null && maskOver != null)
                        maskOver = maskOver.Overlay(overMask, mode: "darken");
                    if (sourceMask != null && maskOver != null)
                        maskOver = maskOver.Overlay(sourceMask.Dynamic().Invert().Invoke(this.Rotate, -info.Angle / 100.0), -info.X, -info.Y, mode: "lighten");
                    var background = over.BilinearResize(finalWidth / 4, finalHeight / 4);
                    for (var i = 0; i < 10; i++)
                        background = background.Blur(1.5);
                    var hybrid = background.GaussResize(finalWidth, finalHeight, p: 3);
                    if (maskOver != null)
                        hybrid = hybrid.Overlay(over.Invoke(this.Rotate, info.Angle / 100.0), finalX + Math.Max(0, info.X), finalY + Math.Max(0, info.Y));
                    if (maskOver == null && info.Angle != 0)
                        maskOver = GetBlankClip(over, true).Dynamic();
                    return hybrid.Overlay(src, finalX + Math.Max(0, -info.X), finalY + Math.Max(0, -info.Y))
                            .Overlay(over.Invoke(this.Rotate, info.Angle / 100.0), finalX + Math.Max(0, info.X), finalY + Math.Max(0, info.Y), mask: maskOver?.Invoke(this.Rotate, info.Angle / 100.0))
                            .Invoke(Downsize, targetSize.Width, targetSize.Height);
                }
                case FramingMode.Mask:
                {
                    src = GetBlankClip((Clip) src, true).Dynamic();
                    if (sourceMask != null)
                        src = src.Overlay(sourceMask, mode: "darken");
                    over = GetBlankClip((Clip) over, true).Dynamic();
                    return src.BlankClip(width: mergedWidth, height: mergedHeight)
                        .Overlay(src, Math.Max(0, -info.X), Math.Max(0, -info.Y))
                        .Overlay(over.Invoke(this.Rotate, info.Angle / 100.0), Math.Max(0, info.X), Math.Max(0, info.Y))
                        .Invoke(Downsize, finalWidth, finalHeight)
                        .AddBorders(finalX, finalY, targetSize.Width - finalX - finalWidth, targetSize.Height - finalY - finalHeight);
                }
                default:
                    throw new AvisynthException();
            }
        }
    }
}
