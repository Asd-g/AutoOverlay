using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutoOverlay;
using AutoOverlay.Filters;
using AvsFilterNet;

[assembly: AvisynthFilterClass(
    typeof(ChangeMatrix), nameof(ChangeMatrix),
    "c[from]s[to]s",
    MtMode.NICE_FILTER)]
namespace AutoOverlay.Filters
{
    public class ChangeMatrix : OverlayFilter
    {
        [AvsArgument]
        public string From { get; private set; }
        [AvsArgument]
        public string To { get; private set; }

        protected override void Initialize(AVSValue args)
        {

        }

        protected override VideoFrame GetFrame(int n)
        {
            var frame = Child.GetFrame(n, StaticEnv);
            var output = NewVideoFrame(StaticEnv);
            var width = frame.GetRowSize() / 2;
            unsafe
            {
                var inY = (ushort*) frame.GetReadPtr(YUVPlanes.PLANAR_Y);
                var inU = (ushort*) frame.GetReadPtr(YUVPlanes.PLANAR_U);
                var inV = (ushort*) frame.GetReadPtr(YUVPlanes.PLANAR_V);
                var outY = (ushort*) output.GetWritePtr(YUVPlanes.PLANAR_Y);
                var outU = (ushort*) output.GetWritePtr(YUVPlanes.PLANAR_U);
                var outV = (ushort*) output.GetWritePtr(YUVPlanes.PLANAR_V);

                var rec709 = new Converter(0.0722, 0.2126);
                var rec2020 = new Converter(0.0593, 0.2627);

                for (var i = 0; i < frame.GetHeight(); i++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        ushort y0 = inY[x], u0 = inU[x], v0 = inV[x];
                        var r = rec2020.R(y0, u0, v0);
                        var g = rec2020.G(y0, u0, v0);
                        var b = rec2020.B(y0, u0, v0);
                        var y = rec709.Y(r, g, b);
                        var u = rec709.Cb(r, g, b);
                        var v = rec709.Cr(r, g, b);
                        outY[x] = (ushort) Math.Max(0, Math.Min(1023, y));
                        outU[x] = (ushort) Math.Max(0, Math.Min(1023, u));
                        outV[x] = (ushort) Math.Max(0, Math.Min(1023, v));
                    }
                    inY += frame.GetPitch(YUVPlanes.PLANAR_Y)/2;
                    inU += frame.GetPitch(YUVPlanes.PLANAR_U)/2;
                    inV += frame.GetPitch(YUVPlanes.PLANAR_V)/2;
                    outY += output.GetPitch(YUVPlanes.PLANAR_Y)/2;
                    outU += output.GetPitch(YUVPlanes.PLANAR_U)/2;
                    outV += output.GetPitch(YUVPlanes.PLANAR_V)/2;
                }
            }
            frame.Dispose();
            return output;
        }


        public sealed class Converter
        {
            private double yR, yG, yB;
            private double pbR, pbG, pbB = 0.5;
            private double prR = 0.5, prG, prB;

            public Converter(double Kb, double Kr)
            {
                yR = Kr;
                yB = Kb;
                yG = 1 - Kr - Kb;
                var divider = 2 - 2 * Kb;
                pbR = -Kr / divider;
                pbG = (Kr + Kb - 1) / divider;
                pbB = (1 - Kb) / divider;
                divider = 2 - 2 * Kr;
                prB = -Kb / divider;
                prG = (Kr + Kb - 1) / divider;
                prR = (1 - Kr) / divider;
            }

            public double Y(double r, double g, double b) => 64 + (yR * r + yG * g + yB * b)*(255.0/219);

            public double Cb(double r, double g, double b) => 512 + (pbR * r + pbG * g + pbB * b) * (255.0 / 112);

            public double Cr(double r, double g, double b) => 512 + (prR * r + prG * g + prB * b) * (255.0 / 112);

            public double R(double y, double cb, double cr) => (255.0 / 219) * (y - 64) + (255.0 / 112) * (1 - yR) * (cr - 512);

            public double G(double y, double cb, double cr) =>
                (255.0 / 219) * (y - 64) -
                (255.0 / 112) * (1 - yB) * (yB / yG) * (cb - 512) -
                (255.0 / 112) * (1 - yR) * (yR / yG) * (cr - 512);

            public double B(double y, double cb, double cr) => (255.0 / 219) * (y - 64) + (255.0 / 112) * (1 - yB) * (cb - 512);
        }
    }
}
