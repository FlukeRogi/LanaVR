# Generates the two placeholder skies used by tester builds into skybox\tester. They are made entirely by this
# script, so they carry no licence restrictions. Both are 2:1 panoramas that repeat seamlessly left to right.
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'skybox\tester'
New-Item -ItemType Directory -Force $out | Out-Null
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

public static class TesterSky
{
    const int Period = 16;
    static readonly float[,] Grid = MakeGrid();

    static float[,] MakeGrid()
    {
        var random = new Random(4117);
        // Wide enough for the finest cloud layer, whose period is 16 doubled five times.
        var grid = new float[512, 256];
        for (int y = 0; y < 256; y++) for (int x = 0; x < 512; x++) grid[x, y] = (float)random.NextDouble();
        return grid;
    }

    static float Smooth(float t) { return t * t * (3 - 2 * t); }

    // Value noise that repeats every 'period' cells across, which keeps the panorama seamless at its back.
    static float Noise(float x, float y, int period)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float fx = Smooth(x - x0), fy = Smooth(y - y0);
        int xa = ((x0 % period) + period) % period, xb = (xa + 1) % period, ya = y0 & 255, yb = (ya + 1) & 255;
        float top = Grid[xa, ya] + (Grid[xb, ya] - Grid[xa, ya]) * fx;
        float bottom = Grid[xa, yb] + (Grid[xb, yb] - Grid[xa, yb]) * fx;
        return top + (bottom - top) * fy;
    }

    static float Clouds(float u, float v)
    {
        float sum = 0, weight = 0.5f;
        int period = Period;
        for (int octave = 0; octave < 6; octave++)
        {
            sum += weight * Noise(u * period, v * period * 0.55f, period);
            weight *= 0.5f;
            period *= 2;
        }
        return sum;
    }

    static float Clamp01(float v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
    static float Mix(float a, float b, float t) { return a + (b - a) * t; }
    static byte Byte(float v) { return (byte)(Clamp01(v) * 255f + 0.5f); }

    public static void Save(string path, int width, bool night)
    {
        int height = width / 2;
        var pixels = new byte[width * height * 4];
        var stars = new Random(907);
        Parallel.For(0, height, y =>
        {
            float elevation = 1f - (y + 0.5f) / height * 2f;  // 1 at the top, 0 at the horizon, -1 at the bottom
            float up = Clamp01(elevation);
            float toZenith = (float)Math.Pow(up, 0.55);
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width, v = (y + 0.5f) / height;
                float r, g, b;
                if (night) { r = Mix(0.20f, 0.03f, toZenith); g = Mix(0.24f, 0.05f, toZenith); b = Mix(0.42f, 0.17f, toZenith); }
                else { r = Mix(0.78f, 0.15f, toZenith); g = Mix(0.91f, 0.47f, toZenith); b = Mix(0.95f, 0.76f, toZenith); }
                // Clouds thin out towards the zenith and the horizon. The sky below the horizon stays plain.
                float band = Clamp01(up * 12f) * Clamp01((0.75f - up) * 3f);
                float amount = Clamp01((Clouds(u, v) - 0.5f) * 6f) * band;
                float dither = (Grid[x & 255, y & 255] - 0.5f) / 255f;
                float cr = night ? 0.26f : 1f, cg = night ? 0.30f : 0.99f, cb = night ? 0.44f : 0.96f;
                int i = (y * width + x) * 4;
                pixels[i] = Byte(Mix(b, cb, amount) + dither);
                pixels[i + 1] = Byte(Mix(g, cg, amount) + dither);
                pixels[i + 2] = Byte(Mix(r, cr, amount) + dither);
                pixels[i + 3] = 255;
            }
        });
        if (night)
            for (int s = 0; s < width; s++)
            {
                int x = stars.Next(width), y = stars.Next(height / 2 - height / 20);
                byte level = (byte)stars.Next(130, 256);
                int i = (y * width + x) * 4;
                pixels[i] = level; pixels[i + 1] = level; pixels[i + 2] = level;
            }
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);
            bitmap.Save(path, ImageFormat.Png);
        }
    }
}
'@
[TesterSky]::Save((Join-Path $out 'tester_day.png'), 4096, $false)
[TesterSky]::Save((Join-Path $out 'tester_night.png'), 4096, $true)
# The tester mapping is the normal mapping with the placeholder images swapped in, so it can't fall out of step.
$map = Get-Content (Join-Path $out '..\skyboxes.txt') | ForEach-Object { $_ -replace '=\s*backdropnight\.png', '= tester_night.png' -replace '=\s*backdrop\.png', '= tester_day.png' }
Set-Content (Join-Path $out 'skyboxes.txt') $map -Encoding ascii
Get-ChildItem $out | ForEach-Object { '{0} {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
