using SkiaSharp;
using System;
using System.IO;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    public class RealtimeEffects
    {
        public static async Task<byte[]> ApplyBrightnessContrast(byte[] imageData, float brightness, float contrast)
        {
            return await Task.Run(() =>
            {
                using (var inputStream = new MemoryStream(imageData))
                using (var original = SKBitmap.Decode(inputStream))
                {
                    // Primena svetline i kontrasta
                    float brightnessValue = brightness / 100f;
                    float contrastValue = (contrast / 100f) + 1f;

                    var matrix = new float[]
                    {
                        contrastValue, 0, 0, 0, brightnessValue,
                        0, contrastValue, 0, 0, brightnessValue,
                        0, 0, contrastValue, 0, brightnessValue,
                        0, 0, 0, 1, 0
                    };

                    var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
                    var paint = new SKPaint { ColorFilter = colorFilter };

                    using (var surface = new SKCanvas(original))
                    {
                        surface.DrawBitmap(original, 0, 0, paint);
                    }

                    using (var outputStream = new MemoryStream())
                    {
                        original.Encode(outputStream, SKEncodedImageFormat.Jpeg, 90);
                        return outputStream.ToArray();
                    }
                }
            });
        }

        public static async Task<byte[]> ApplyBlur(byte[] imageData, float blurAmount)
        {
            return await Task.Run(() =>
            {
                using (var inputStream = new MemoryStream(imageData))
                using (var original = SKBitmap.Decode(inputStream))
                {
                    float sigma = blurAmount / 10f; // 0-20 -> 0-2
                    var imageFilter = SKImageFilter.CreateBlur(sigma, sigma);
                    var paint = new SKPaint { ImageFilter = imageFilter };

                    using (var surface = new SKCanvas(original))
                    {
                        surface.DrawBitmap(original, 0, 0, paint);
                    }

                    using (var outputStream = new MemoryStream())
                    {
                        original.Encode(outputStream, SKEncodedImageFormat.Jpeg, 90);
                        return outputStream.ToArray();
                    }
                }
            });
        }

        public static async Task<byte[]> ApplySepia(byte[] imageData)
        {
            return await Task.Run(() =>
            {
                using (var inputStream = new MemoryStream(imageData))
                using (var original = SKBitmap.Decode(inputStream))
                {
                    var matrix = new float[]
                    {
                        0.393f, 0.769f, 0.189f, 0, 0,
                        0.349f, 0.686f, 0.168f, 0, 0,
                        0.272f, 0.534f, 0.131f, 0, 0,
                        0, 0, 0, 1, 0
                    };

                    var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
                    var paint = new SKPaint { ColorFilter = colorFilter };

                    using (var surface = new SKCanvas(original))
                    {
                        surface.DrawBitmap(original, 0, 0, paint);
                    }

                    using (var outputStream = new MemoryStream())
                    {
                        original.Encode(outputStream, SKEncodedImageFormat.Jpeg, 90);
                        return outputStream.ToArray();
                    }
                }
            });
        }

        public static async Task<byte[]> ApplyNegative(byte[] imageData)
        {
            return await Task.Run(() =>
            {
                using (var inputStream = new MemoryStream(imageData))
                using (var original = SKBitmap.Decode(inputStream))
                {
                    var matrix = new float[]
                    {
                        -1, 0, 0, 0, 255,
                        0, -1, 0, 0, 255,
                        0, 0, -1, 0, 255,
                        0, 0, 0, 1, 0
                    };

                    var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
                    var paint = new SKPaint { ColorFilter = colorFilter };

                    using (var surface = new SKCanvas(original))
                    {
                        surface.DrawBitmap(original, 0, 0, paint);
                    }

                    using (var outputStream = new MemoryStream())
                    {
                        original.Encode(outputStream, SKEncodedImageFormat.Jpeg, 90);
                        return outputStream.ToArray();
                    }
                }
            });
        }
    }
}