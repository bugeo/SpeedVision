namespace TrafficSpeedAnalyzer.Video.Tests;

/// <summary>Lettura dei file di vidstab e controlli della stabilizzazione, senza FFmpeg.</summary>
public class StabilizationParsingTests
{
    // Estratto reale di un file prodotto da vidstabdetect (tripod=1) su un video mosso.
    private const string Trf = """
        VID.STAB 1
        #      accuracy = 15
        #     shakiness = 10
        #      stepsize = 4
        #   mincontrast = 0.050000
        Frame 1 (List 0 [])
        Frame 2 (List 5 [(LM -4 -6 89 278 48 0.110041 0.299479),(LM -4 -5 566 278 48 0.149547 0.276910),(LM -4 -5 354 341 48 0.134141 0.404080),(LM -5 -6 94 102 16 0.069535 0.308594),(LM 30 12 536 292 16 0.141677 0.289062)])
        Frame 3 (List 2 [(LM 1 0 89 278 48 0.11 0.27),(LM 2 0 566 278 48 0.15 0.32)])
        """;

    [Fact]
    public void Parse_ReturnsMedianMotionPerFrame_IgnoringOutliers()
    {
        List<StabFrameMotion> m = VidStabFile.Parse(Trf.Replace("\n", "\r\n"));
        Assert.Equal(3, m.Count);

        Assert.Equal(1, m[0].Frame);
        Assert.Equal(0, m[0].Fields);
        Assert.True(double.IsNaN(m[0].Magnitude));

        // Il quinto campo (un veicolo che passa: 30, 12) non sposta la mediana.
        Assert.Equal(5, m[1].Fields);
        Assert.Equal(-4, m[1].Dx);
        Assert.Equal(-5, m[1].Dy);

        Assert.Equal(1.5, m[2].Dx);
        Assert.Equal(0, m[2].Dy);
    }

    [Fact]
    public void Report_SummarizesShakeAndMisalignedFrames()
    {
        const int w = 1000, h = 500;
        var before = new List<FrameMotion> { FrameMotion.Identity(0, 100) };
        var after = new List<FrameMotion> { FrameMotion.Identity(0, 100) };
        for (int i = 1; i < 100; i++)
        {
            before.Add(FrameMotion.FromSimilarity(i, 1, 0, i % 10, 0, 80, 90, i != 50));   // traslazione 0…9 px
            after.Add(FrameMotion.FromSimilarity(i, 1, 0, i == 70 ? 2 : 0, 0, 80, 90, true));
        }

        StabilizationReport r = VideoStabilizer.BuildReport(before, after, w, h, "x.mp4", 10, 100, 3.5);
        Assert.Equal(9, r.ShakeBeforeMaxPx, 9);
        Assert.Equal(9, r.ShakeBeforeP95Px, 9);
        Assert.Equal(2, r.ResidualMaxPx, 9);
        Assert.Equal([70], r.MisalignedFrames);
        Assert.Equal(1, r.WeakFrames);
        Assert.False(r.FromCache);
    }

    [Fact]
    public void FrameMotion_RotationShowsUpAtTheCorners()
    {
        // Rotazione di 0,5° attorno al centro di un'immagine 1000 × 500: il centro resta fermo, gli angoli no.
        double a = 0.5 * Math.PI / 180, cx = 500, cy = 250;
        double tx = cx - (Math.Cos(a) * cx) + (Math.Sin(a) * cy), ty = cy - (Math.Sin(a) * cx) - (Math.Cos(a) * cy);
        FrameMotion m = FrameMotion.FromSimilarity(1, Math.Cos(a), Math.Sin(a), tx, ty, 50, 50, true);
        Assert.Equal(0, m.DisplacementAt(cx, cy), 9);
        Assert.Equal(Math.Sqrt((cx * cx) + (cy * cy)) * 2 * Math.Sin(a / 2), m.MaxDisplacement(1000, 500), 6);
        Assert.Equal(0.5, m.AngleDegrees, 9);
    }

    /// <summary>Valutatore minimo delle espressioni prodotte (numeri, if(lt(in,N),a,b)).</summary>
    private static double Eval(string e, int n, ref int pos)
    {
        if (e.AsSpan(pos).StartsWith("if(lt(in,"))
        {
            pos += 9;
            int comma = e.IndexOf(')', pos);
            int limit = int.Parse(e[pos..comma], System.Globalization.CultureInfo.InvariantCulture);
            pos = comma + 2;                       // "),"
            double a = Eval(e, n, ref pos);
            pos++;                                 // ","
            double b = Eval(e, n, ref pos);
            pos++;                                 // ")"
            return n < limit ? a : b;
        }

        int end = pos;
        while (end < e.Length && (char.IsDigit(e[end]) || e[end] is '.' or '-'))
        {
            end++;
        }

        double v = double.Parse(e[pos..end], System.Globalization.CultureInfo.InvariantCulture);
        pos = end;
        return v;
    }

    [Fact]
    public void FromVidStab_ConvertsMediansAndMarksWeakFrames()
    {
        List<StabFrameMotion> m = VidStabFile.Parse(Trf);
        FrameMotion[] f = VideoStabilizer.FromVidStab(m, 3);
        Assert.True(f[0].Reliable);
        Assert.Equal(0, f[0].DisplacementAt(10, 10), 9);
        Assert.Equal((6.0, 5.0), f[1].Apply(10, 10));   // mediana −4, −5
        Assert.True(f[1].Reliable);                     // 5 campi
        Assert.False(f[2].Reliable);                    // 2 campi soltanto
        Assert.Throws<StabilizationException>(() => VideoStabilizer.FromVidStab(m, 100));
    }

    [Fact]
    public void SameTimes_AcceptsConstantOffset_RejectsChangedOrMissingFrames()
    {
        double[] t = Enumerable.Range(0, 50).Select(i => (i / 30.0) + (i % 7 == 0 ? 0.004 : 0)).ToArray();
        var source = new FrameIndex(t);

        VideoStabilizer.CheckSameTimes(source, new FrameIndex(t.Select(x => x + 0.0005)));
        VideoStabilizer.CheckSameTimes(source, new FrameIndex(t.Select(x => x + 1.0))); // scarto costante

        double[] changed = (double[])t.Clone();
        changed[20] += 0.01;
        Assert.Throws<StabilizationException>(() => VideoStabilizer.CheckSameTimes(source, new FrameIndex(changed)));
        Assert.Throws<StabilizationException>(() => VideoStabilizer.CheckSameTimes(source, new FrameIndex(t.Take(49))));
    }
}
