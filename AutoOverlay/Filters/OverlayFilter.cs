﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using AvsFilterNet;

namespace AutoOverlay
{
    public abstract class OverlayFilter : AvisynthFilter
    {
        public virtual bool Debug { get; protected set; }

        protected dynamic DynamicEnv => DynamicEnviroment.Env;
        protected ScriptEnvironment StaticEnv => DynamicEnviroment.Env;

        private static ISet<OverlayFilter> Filters { get; } = new HashSet<OverlayFilter>();

        protected OverlayFilter()
        {
            Filters.Add(this);
        }

        public sealed override void Initialize(AVSValue args, ScriptEnvironment env)
        {
            using (new DynamicEnviroment(env))
            {
                try
                {
                    OverlayUtils.InitArgs(this, args);
                    Initialize(args);
                    base.Initialize(args, env);
                }
                catch
                {
                    DisposeAll();
                    throw;
                }
            }

            AfterInitialize(env);
        }

        protected virtual void Initialize(AVSValue args)
        {
            var vi = new VideoInfo
            {
                width = 640,
                height = 320,
                pixel_type = ColorSpaces.CS_BGR32,
                fps_numerator = 25,
                fps_denominator = 1,
                num_frames = 1
            };
            SetVideoInfo(ref vi);
        }

        protected virtual void AfterInitialize(ScriptEnvironment env)
        {
        }

        public sealed override VideoFrame GetFrame(int n, ScriptEnvironment env)
        {
            using (new DynamicEnviroment(env))
            {
                try
                {
                    return GetFrame(n);
                }
                catch
                {
                    DisposeAll();
                    throw;
                }
            }
        }

        protected virtual VideoFrame GetFrame(int n)
        {
            return Debug ? GetSubtitledFrame(ToString()) : NewVideoFrame(StaticEnv);
        }

        protected VideoFrame GetSubtitledFrame(string text)
        {
            var blank = DynamicEnv.BlankClip(width: GetVideoInfo().width, height: GetVideoInfo().height);
            var subtitled = blank.Subtitle(text.Replace("\n", "\\n"), align: 8, lsp: 0, size: 24);
            return subtitled[0];
        }

        protected Clip GetBlankClip(Clip clip, bool white)
        {
            if (clip.GetVideoInfo().pixel_type.HasFlag(ColorSpaces.CS_PLANAR | ColorSpaces.CS_INTERLEAVED))
                return DynamicEnv.BlankClip(clip, color_yuv: white ? 0xFF0000 : 0x000000);
            if (clip.GetVideoInfo().pixel_type.HasFlag(ColorSpaces.CS_PLANAR))
                return DynamicEnv.BlankClip(clip, color_yuv: white ? 0xFF8080 : 0x008080);
            return DynamicEnv.BlankClip(clip, color: white ? 0xFFFFFF : 0);
        }

        public dynamic ResizeRotate(
            Clip clip, 
            string resizeFunc, string rotateFunc, 
            int width, int height, int angle = 0,
            RectangleD crop = default(RectangleD))
        {
            if (clip == null || crop == RectangleD.Empty && width == clip.GetVideoInfo().width && height == clip.GetVideoInfo().height)
                return clip.Dynamic();

            var intCrop = Rectangle.FromLTRB(
                (int) Math.Floor(crop.Left),
                (int) Math.Floor(crop.Top),
                (int) Math.Floor(crop.Right),
                (int) Math.Floor(crop.Bottom)
            );
            if (!intCrop.IsEmpty)
            {
                clip = DynamicEnv.Crop(clip, intCrop.Left, intCrop.Top, -intCrop.Right, -intCrop.Bottom);
                crop = RectangleD.FromLTRB(
                    crop.Left - intCrop.Left,
                    crop.Top - intCrop.Top,
                    crop.Right - intCrop.Right,
                    crop.Bottom - intCrop.Bottom
                );
            }

            dynamic resized;
            if (crop == RectangleD.Empty)
                resized = clip.Dynamic().Invoke(resizeFunc, width, height);
            else resized = clip.Dynamic().Invoke(resizeFunc, width, height,
                src_left: crop.Left, src_top: crop.Top, 
                src_width: -crop.Right, src_height: -crop.Bottom);
            if (angle == 0)
                return resized;
            return resized.Invoke(rotateFunc, angle / 100.0);
        }

        protected void Log(Func<string> supplier)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine(supplier());
#endif
        }

        protected void Log(string format, params object[] args)
        {
#if DEBUG
            if (args.Length == 0)
                System.Diagnostics.Debug.WriteLine(format);
            else
                System.Diagnostics.Debug.WriteLine(format, args);
#endif
        }

        protected override void Dispose(bool A_0)
        {
            Filters.Remove(this);
            OverlayUtils.Dispose(this);
            base.Dispose(A_0);
        }

        private void DisposeAll()
        {
            foreach (var filter in Filters.ToArray())
                filter.Dispose();
        }
    }
}
