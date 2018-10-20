using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoOverlay;
using AvsFilterNet;

[assembly: AvisynthFilterClass(typeof(SmartJoin),
    nameof(SmartJoin),
    "cc+s[range]i[limit]f[planes]s[analyze]b",
    MtMode.NICE_FILTER)]
namespace AutoOverlay
{
    public class SmartJoin : AvisynthFilter
    {
        private Clip[] sources;
        private int range = 50;
        private double limit = 0;
        private YUVPlanes[] planes;
        private bool analysisPass = false;
        private int[] data;
        private StreamWriter writer;
        private int[] prevFrames;
        private int currentFrame = -1;
        private int lastFrame = -1;

        public override void Initialize(AVSValue args, ScriptEnvironment env)
        {
            Child.SetCacheHints(CacheType.CACHE_25_ALL, range);
            sources = new Clip[args[1].ArraySize()];
            prevFrames = Enumerable.Repeat(-1, sources.Length).ToArray();
            for (var i = 0; i < sources.Length; i++)
            {
                sources[i] = args[1][i].AsClip();
                sources[i].SetCacheHints(CacheType.CACHE_25_ALL, range);
            }
            var path = args[2].AsString();
            range = args[3].AsInt(range);
            limit = args[4].AsFloat(limit);
            planes = args[5].AsString("yuv").ToCharArray().Select(p => Enum.Parse(typeof(YUVPlanes), "PLANAR_" + p, true)).Cast<YUVPlanes>().ToArray();
            analysisPass = args[6].AsBool(analysisPass);
            if (analysisPass)
                writer = File.CreateText(path);
            else
            {
                var lines = File.Exists(path) ? File.ReadAllLines(path).Where(p => !string.IsNullOrEmpty(p)).ToArray() : new string[0];
                data = new int[lines.Length + Child.GetVideoInfo().num_frames];
                var vi = GetVideoInfo();
                vi.num_frames = data.Length;
                SetVideoInfo(ref vi);
                var currentFrame = 0;
                var mainSrcFrame = 0;
                foreach (var line in lines)
                {
                    var splits = line.Split(' ');
                    if (splits.Length < 3
                        || !int.TryParse(splits[0], out var targetFrame)
                        || !int.TryParse(splits[1], out var source)
                        || !int.TryParse(splits[2], out var srcFrame))
                        throw new AvisynthException("Invalid file");
                    while (currentFrame < targetFrame)
                    {
                        data[currentFrame++] = mainSrcFrame++ << 8;
                    }
                    data[currentFrame++] = (srcFrame << 8) + source;
                }
                while (currentFrame < data.Length)
                {
                    data[currentFrame++] = mainSrcFrame++ << 8;
                }
            }
        }

        private bool IsEqual(VideoFrame a, VideoFrame b)
        {
            var equal = true;
            Parallel.ForEach(planes, plane =>
            {
                unsafe
                {
                    var diff = NativeUtils.SquaredDifferenceSum(
                        (byte*) a.GetReadPtr(plane), a.GetPitch(plane),
                        (byte*) b.GetReadPtr(plane), b.GetPitch(plane),
                        a.GetRowSize(plane), a.GetHeight(plane));
                    if (diff >= limit + double.Epsilon)
                        equal = false;
                }
            });
            return equal;
        }

        public override VideoFrame GetFrame(int n, ScriptEnvironment env)
        {
            n = Math.Min(data.Length - 1, n);
            if (analysisPass)
            {
                currentFrame++;
                var frame = Child.GetFrame(n, env);
                // find the same in other sources
                for (var i = 0; i < sources.Length; i++)
                {
                    var src = sources[i];
                    var prevFrame = prevFrames[i];
                    var endFrame = prevFrame + range;
                    for (var srcN = prevFrame + 1; srcN <= endFrame && srcN < src.GetVideoInfo().num_frames; srcN++)
                    {
                        using (var srcFrame = src.GetFrame(srcN, env))
                        {
                            if (IsEqual(frame, srcFrame))
                            {
                                var delta = srcN - prevFrame;
                                if (delta > 1)
                                {
                                    var bad = currentFrame - lastFrame > 1 ? " bad" : "";
                                    for (var addFrame = prevFrame + 1; addFrame < srcN; addFrame++)
                                    {
                                        writer.WriteLine($"{currentFrame++} {i + 1} {addFrame}{bad}");
                                        writer.Flush();
                                    }
                                }
                                prevFrames[i] = srcN;
                                lastFrame = currentFrame;
                                break;
                            }

                        }
                    }
                    
                }
                return frame;
            }
            else
            {
                var info = data[n];
                var frame = info >> 8;
                var src = unchecked ((byte) info);
                //Debug.WriteLine($"{src}: {frame}");
                return (src == 0 ? Child : sources[src - 1]).GetFrame(frame, env);
            }
        }

        protected override void Dispose(bool A_0)
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
            base.Dispose(A_0);
        }
    }
}
