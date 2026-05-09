using System;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace UltraVideoEditor
{
    /// <summary>
    /// Kreira animirani video iz teksta koristeći SkiaSharp + FFmpeg.
    /// Sve je tekstualno konfigurisano - nema vizuelnog alata.
    /// </summary>
    public static class SkiaAnimationEngine
    {
        public const int WIDTH  = 1920;
        public const int HEIGHT = 1080;
        public const int FPS    = 30;

        public static void RenderToVideo(
            string text,
            string style,
            string textColor,
            string bgColor,
            double durationSeconds,
            string outputPath)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "SkiaAnim_" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(tempDir);

            try
            {
                int totalFrames = (int)(durationSeconds * FPS);
                SKColor bg  = ParseColor(bgColor,  SKColors.Black);
                SKColor txt = ParseColor(textColor, SKColors.White);

                for (int frame = 0; frame < totalFrames; frame++)
                {
                    double t = (double)frame / totalFrames; // 0.0 → 1.0
                    using var surface = SKSurface.Create(new SKImageInfo(WIDTH, HEIGHT));
                    var canvas = surface.Canvas;

                    // Pozadina
                    canvas.Clear(bg);

                    RenderFrame(canvas, text, style, txt, bg, t, frame, totalFrames);

                    // Sačuvaj frame kao PNG
                    using var image = surface.Snapshot();
                    using var data  = image.Encode(SKEncodedImageFormat.Png, 95);
                    string framePath = Path.Combine(tempDir, $"frame_{frame:D6}.png");
                    File.WriteAllBytes(framePath, data.ToArray());
                }

                // FFmpeg konkatenira frejmove u video
                string ffmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
                string args   = $"-nostdin -framerate {FPS} -i \"{tempDir}\\frame_%06d.png\" " +
                                $"-c:v libx264 -pix_fmt yuv420p -y \"{outputPath}\"";

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName               = ffmpeg,
                        Arguments              = args,
                        CreateNoWindow         = true,
                        UseShellExecute        = false,
                        RedirectStandardError  = true,
                        RedirectStandardOutput = true,
                        WorkingDirectory       = Path.GetTempPath()
                    }
                };
                proc.Start();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit();
                stderrTask.Wait();
                stdoutTask.Wait();

                if (proc.ExitCode != 0)
                    throw new Exception(string.Format(LanguageManager.GetText("sae_ffmpeg_error", (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr"), stderrTask.Result.Substring(0, Math.Min(300, stderrTask.Result.Length))));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static void RenderFrame(
            SKCanvas canvas, string text, string style,
            SKColor txtColor, SKColor bgColor,
            double t, int frame, int totalFrames)
        {
            switch (style)
            {
                case "FadeIn":
                    RenderFadeIn(canvas, text, txtColor, t);
                    break;
                case "SlideLeft":
                    RenderSlideLeft(canvas, text, txtColor, t);
                    break;
                case "SlideUp":
                    RenderSlideUp(canvas, text, txtColor, t);
                    break;
                case "Bounce":
                    RenderBounce(canvas, text, txtColor, t);
                    break;
                case "ZoomIn":
                    RenderZoomIn(canvas, text, txtColor, t);
                    break;
                case "Stars":
                    RenderStars(canvas, text, txtColor, bgColor, t);
                    break;
                case "Rainbow":
                    RenderRainbow(canvas, text, t);
                    break;
                default:
                    RenderFadeIn(canvas, text, txtColor, t);
                    break;
            }
        }

        // ── Stilovi ──────────────────────────────────────────────────

        private static void RenderFadeIn(SKCanvas c, string text, SKColor col, double t)
        {
            // Fade in prvih 30%, puno vidljivo, fade out zadnjih 20%
            float alpha;
            if      (t < 0.3) alpha = (float)(t / 0.3);
            else if (t > 0.8) alpha = (float)((1.0 - t) / 0.2);
            else              alpha = 1.0f;

            using var paint = MakeTextPaint(col.WithAlpha((byte)(alpha * 255)), 120);
            DrawCenteredText(c, text, paint);
        }

        private static void RenderSlideLeft(SKCanvas c, string text, SKColor col, double t)
        {
            // Ulazi s desna, stoji, izlazi ulevo
            float x;
            if      (t < 0.2) x = WIDTH + (float)((t / 0.2 - 1.0) * WIDTH);  // ulaz
            else if (t > 0.8) x = (float)(-(t - 0.8) / 0.2 * WIDTH);          // izlaz
            else              x = 0;

            c.Save();
            c.Translate(x, 0);
            using var paint = MakeTextPaint(col, 120);
            DrawCenteredText(c, text, paint);
            c.Restore();
        }

        private static void RenderSlideUp(SKCanvas c, string text, SKColor col, double t)
        {
            float y;
            if      (t < 0.2) y = HEIGHT + (float)((t / 0.2 - 1.0) * HEIGHT * 0.5f);
            else if (t > 0.8) y = (float)(-(t - 0.8) / 0.2 * HEIGHT * 0.5f);
            else              y = 0;

            c.Save();
            c.Translate(0, y);
            using var paint = MakeTextPaint(col, 120);
            DrawCenteredText(c, text, paint);
            c.Restore();
        }

        private static void RenderBounce(SKCanvas c, string text, SKColor col, double t)
        {
            // Tekst "skace" gore-dole
            double bounce = Math.Sin(t * Math.PI * 6) * 80 * Math.Exp(-t * 3);
            c.Save();
            c.Translate(0, (float)bounce);
            using var paint = MakeTextPaint(col, 130);
            DrawCenteredText(c, text, paint);
            c.Restore();
        }

        private static void RenderZoomIn(SKCanvas c, string text, SKColor col, double t)
        {
            float scale;
            if      (t < 0.3) scale = (float)(t / 0.3) * 1.0f;
            else if (t > 0.7) scale = 1.0f + (float)((t - 0.7) / 0.3) * 0.5f;
            else              scale = 1.0f;

            c.Save();
            c.Translate(WIDTH / 2f, HEIGHT / 2f);
            c.Scale(scale);
            c.Translate(-WIDTH / 2f, -HEIGHT / 2f);

            float alpha = t < 0.3f ? (float)(t / 0.3) : 1.0f;
            using var paint = MakeTextPaint(col.WithAlpha((byte)(alpha * 255)), 120);
            DrawCenteredText(c, text, paint);
            c.Restore();
        }

        private static void RenderStars(SKCanvas c, string text, SKColor txtCol, SKColor bgCol, double t)
        {
            // Zvezde u pozadini + tekst
            var rng = new Random(42);
            using var starPaint = new SKPaint { Color = SKColors.Yellow.WithAlpha(180), IsAntialias = true };
            for (int i = 0; i < 80; i++)
            {
                float x = (float)(rng.NextDouble() * WIDTH);
                float y = (float)(rng.NextDouble() * HEIGHT);
                float r = (float)(rng.NextDouble() * 3 + 1);
                float twinkle = (float)(0.5 + 0.5 * Math.Sin(t * Math.PI * 4 + i));
                starPaint.Color = SKColors.Yellow.WithAlpha((byte)(twinkle * 200));
                c.DrawCircle(x, y, r, starPaint);
            }

            float alpha = t < 0.3 ? (float)(t / 0.3) : (t > 0.8 ? (float)((1 - t) / 0.2) : 1.0f);
            using var paint = MakeTextPaint(txtCol.WithAlpha((byte)(alpha * 255)), 120);
            DrawCenteredText(c, text, paint);
        }

        private static void RenderRainbow(SKCanvas c, string text, double t)
        {
            // Svako slovo drugom bojom dugine
            SKColor[] rainbow = {
                SKColors.Red, new SKColor(255,127,0), SKColors.Yellow,
                SKColors.Green, SKColors.Blue, new SKColor(75,0,130), new SKColor(148,0,211)
            };

            using var paint = MakeTextPaint(SKColors.White, 130);
            float totalWidth = paint.MeasureText(text);
            float x = (WIDTH - totalWidth) / 2f;
            float y = HEIGHT / 2f + 50;

            // Rotacija boja sa vremenom
            int offset = (int)(t * 7);
            for (int i = 0; i < text.Length; i++)
            {
                paint.Color = rainbow[(i + offset) % rainbow.Length];
                c.DrawText(text[i].ToString(), x, y, paint);
                x += paint.MeasureText(text[i].ToString());
            }
        }

        // ── Helpers ──────────────────────────────────────────────────

        private static SKPaint MakeTextPaint(SKColor color, float size)
        {
            return new SKPaint
            {
                Color       = color,
                TextSize    = size,
                IsAntialias = true,
                Typeface    = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold),
                TextAlign   = SKTextAlign.Center
            };
        }

        private static void DrawCenteredText(SKCanvas c, string text, SKPaint paint)
        {
            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            float y = HEIGHT / 2f - bounds.MidY;
            c.DrawText(text, WIDTH / 2f, y, paint);
        }

        private static SKColor ParseColor(string hex, SKColor fallback)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6)
                {
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                    return new SKColor(r, g, b);
                }
            }
            catch { }
            return fallback;
        }
    }
}
