﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Text;
using Windows.Foundation.Collections;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.System.Threading;

namespace VideoEffects
{
    public sealed class ExtractVideoEffect : IBasicVideoEffect
    {
        private CanvasDevice canvasDevice;
        private Matrix5x4 desaturate = new Matrix5x4
        {
            M11 = 1,
            M12 = 0,
            M13 = 0,
            M14 = 0,
            M21 = 0,
            M22 = 0,
            M23 = 0,
            M24 = 0,
            M31 = 0,
            M32 = 0,
            M33 = 1,
            M34 = 0,
            M41 = 0,
            M42 = 0,
            M43 = 0,
            M44 = 1,
            M51 = 0,
            M52 = 0,
            M53 = -0.5f,
            M54 = 0
        };
        Vector2 brightnessWhitePoint = new Vector2(0.5f, 1);
        private Rect subtitleRect;
        private StorageFolder folder;
        private CanvasBitmap bitmap;
        private StorageFile recentFile;
        private string recentFilename = string.Empty;
        Color[] prevPixels;
        int prevTime;

        public bool IsReadOnly { get { return false; } }

        public IReadOnlyList<VideoEncodingProperties> SupportedEncodingProperties { get { return new List<VideoEncodingProperties>(); } }

        public MediaMemoryTypes SupportedMemoryTypes { get { return MediaMemoryTypes.Gpu; } }

        public bool TimeIndependent { get { return false; } }

        public void Close(MediaEffectClosedReason reason)
        {
        }

        public void DiscardQueuedFrames()
        {
        }

        public void SetProperties(IPropertySet configuration)
        {
            object value;
            if (configuration.TryGetValue("SubtitleRect", out value))
                subtitleRect = (Rect)value;

            if (configuration.TryGetValue("VideoFilename", out value))
            {
                ApplicationData.Current.TemporaryFolder.GetFolderAsync((string)value).Completed = new AsyncOperationCompletedHandler<StorageFolder>((getInfo, getStatus) =>
                {
                    if (getStatus == AsyncStatus.Completed)
                    {
                        folder = getInfo.GetResults();
                    }
                    else
                    {
                        ApplicationData.Current.TemporaryFolder.CreateFolderAsync((string)value).Completed = new AsyncOperationCompletedHandler<StorageFolder>((createInfo, createStatus) =>
                        {
                            if (createStatus == AsyncStatus.Completed)
                                folder = createInfo.GetResults();
                            else
                                Debug.Assert(false);
                        });
                    }
                });
            }
        }

        public void SetEncodingProperties(VideoEncodingProperties encodingProperties, IDirect3DDevice device)
        {
            canvasDevice = CanvasDevice.CreateFromDirect3D11Device(device);
        }
        
        public async void ProcessFrame(ProcessVideoFrameContext context)
        {
            var inputSurface = context.InputFrame.Direct3DSurface;
            var outputSurface = context.OutputFrame.Direct3DSurface;

            TimeSpan ts = context.InputFrame.RelativeTime.Value;
            Color[] curPixels = null;
            int width, height;

            using (CanvasBitmap inputBitmap = CanvasBitmap.CreateFromDirect3D11Surface(canvasDevice, inputSurface))
            using (CanvasRenderTarget renderTarget = CanvasRenderTarget.CreateFromDirect3D11Surface(canvasDevice, outputSurface))
            using (CanvasDrawingSession ds = renderTarget.CreateDrawingSession())
            using (CropEffect cropEffect = new CropEffect { Source = inputBitmap, SourceRectangle = subtitleRect })
            using (ContrastEffect contrastEffect = new ContrastEffect { Source = cropEffect, Contrast = 1 })
            using (MorphologyEffect morphologyEffect = new MorphologyEffect { Source = contrastEffect, Mode = MorphologyEffectMode.Dilate, Width = 2 })
            using (PosterizeEffect posterizeEffect = new PosterizeEffect { Source = contrastEffect, BlueValueCount = 2, GreenValueCount = 2, RedValueCount = 2 })
            using (RgbToHueEffect rgbToHueEffect = new RgbToHueEffect { Source = posterizeEffect, OutputColorSpace = EffectHueColorSpace.Hsl })
            using (ColorMatrixEffect colorMatrixEffect = new ColorMatrixEffect { Source = rgbToHueEffect, ColorMatrix = desaturate })
            using (HueToRgbEffect hueToRgbEffect = new HueToRgbEffect { Source = colorMatrixEffect, SourceColorSpace = EffectHueColorSpace.Hsl })
            using (BrightnessEffect brightnessEffect = new BrightnessEffect { Source = hueToRgbEffect, WhitePoint = new Vector2(0.1f, 1) })
            using (InvertEffect invertEffect = new InvertEffect { Source = brightnessEffect })
            using (CompositeEffect composite = new CompositeEffect { Sources = { invertEffect } })
            {
                Rect rtDst = new Rect(0, 0, subtitleRect.Width, subtitleRect.Height);
                ds.DrawImage(composite, rtDst, subtitleRect);

                width = (int)subtitleRect.Width;
                height = (int)subtitleRect.Height;
                curPixels = renderTarget.GetPixelColors(0, 0, width, height);
            }
            // Floodfill to clear the edges
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (y == 0 || y == height - 1 || x == 0 || x == width - 1)
                    {
                        FloodFill(curPixels, width, height, new Point(x, y), Colors.Black, Colors.White);
                    }
                }
            }

            // Crop white
            int newHead = 0;
            for (int y = 0; y < height; y++)
            {
                bool isWhiteLine = true;
                for (int x = 0; x < width; x++)
                {
                    if (curPixels[y * width + x] != Colors.White)
                    {
                        isWhiteLine = false;
                        break;
                    }
                }

                if (isWhiteLine == true)
                    newHead = y + 1;
                else
                    break;
            }

            curPixels = curPixels.Skip(newHead * width).ToArray();

            if (curPixels.Length == 0)
            {
                prevPixels = null;
                prevTime = (int)ts.TotalMilliseconds;
                return;
            }

            double dDiff = CompareFrames(curPixels, prevPixels);
            prevPixels = curPixels;
            prevTime = (int)ts.TotalMilliseconds;
            if (dDiff > 0.995)
            {
                string filename = recentFile.Name;
                int pos = filename.LastIndexOf("-");
                if (pos > 0)
                    filename = filename.Substring(0, pos + 1) + ((int)ts.TotalMilliseconds).ToString("D8") + ".bmp";
                else
                {
                    string sub = filename.Substring(0, filename.LastIndexOf("."));
                    filename = sub + "-" + ((int)ts.TotalMilliseconds).ToString("D8") + ".bmp";
                }
                // Update the name string until the next frame is created, then use this to rename the previous one.
                recentFilename = filename;

                Debug.WriteLine(string.Format("Time {0}, Diff {1}, Filename {2}", ((int)ts.TotalMilliseconds).ToString("D8"), dDiff, recentFilename));
            }
            else
            {
                if (recentFilename != string.Empty)
                {
                    await Task.Run(() => RenameFile(recentFile, recentFilename));
                }
                recentFilename = string.Empty;

                Color[] pixels = new Color[curPixels.Length];
                curPixels.CopyTo(pixels, 0);

                recentFile = await folder.CreateFileAsync(((int)ts.TotalMilliseconds).ToString("D8") + ".bmp", CreationCollisionOption.ReplaceExisting);

                using (var outStream = await recentFile.OpenAsync(FileAccessMode.ReadWrite))
                {
                    bitmap = CanvasBitmap.CreateFromColors(canvasDevice, pixels, (int)subtitleRect.Width, (int)subtitleRect.Height - newHead);
                    await bitmap.SaveAsync(outStream, CanvasBitmapFileFormat.Bmp);
                }

                Debug.WriteLine(string.Format("Time {0}, Diff {1}, Filename {2}", ((int)ts.TotalMilliseconds).ToString("D8"), dDiff, recentFile.Name));
            }
        }

        void RenameFile(StorageFile f, string n)
        {
            f.RenameAsync(n).Completed = new AsyncActionCompletedHandler((renameInfo, renameStatus) => { });            
        }

        private double CompareFrames(Color[] img1, Color[] img2)
        {
            if (img2 == null)
                return 0;

            int offset1 = 0;
            int offset2 = 0;
            if (img1.Length > img2.Length)
                offset1 = img1.Length - img2.Length;
            else if (img2.Length > img1.Length)
                offset2 = img2.Length - img1.Length;

            byte[] same = new byte[img1.Length];
            //colorDiff = new Color[img1.Length];
            double sameCount = 0;
            int length = img1.Length == img2.Length ? img1.Length : Math.Min(img1.Length, img2.Length);
            for (int i = 0; i < length; i++)
            {
                // since the pixels are either black or white, only check the first byte.
                same[i] = (byte)(img1[i + offset1] == img2[i + offset2] ? 1 : 0);
                if (same[i] == 0)
                {
                    //if (img1[i + offset1] != Colors.White)
                    //    colorDiff[i] = Colors.Red;
                    //else if (img2[i + offset2] != Colors.White)
                    //    colorDiff[i] = Colors.Blue;
                }
                else
                {
                    sameCount++;
                }
            }

            return sameCount / length;
        }

        private static void FloodFill(Color[] pixels, int width, int height, Point pt, Color targetColor, Color replacementColor)
        {
            Queue<Point> q = new Queue<Point>();
            q.Enqueue(pt);
            while (q.Count > 0)
            {
                Point n = q.Dequeue();
                if (GetPixel(pixels, width, n.X, n.Y) == replacementColor)
                    continue;
                Point w = n, e = new Point(n.X + 1, n.Y);
                while ((w.X >= 0) && GetPixel(pixels, width, w.X, w.Y) != replacementColor)
                {
                    pixels[(int)(w.X + width * w.Y)] = replacementColor;
                    
                    if ((w.Y > 0) && GetPixel(pixels, width, w.X, w.Y - 1) != replacementColor)
                        q.Enqueue(new Point(w.X, w.Y - 1));
                    if ((w.Y < height - 1) && GetPixel(pixels, width, w.X, w.Y + 1) != replacementColor)
                        q.Enqueue(new Point(w.X, w.Y + 1));
                    w.X--;
                }
                while ((e.X <= width - 1) && GetPixel(pixels, width, e.X, e.Y) != replacementColor)
                {
                    pixels[(int)(e.X + width * e.Y)] = replacementColor;
                    if ((e.Y > 0) && GetPixel(pixels, width, e.X, e.Y - 1) != replacementColor)
                        q.Enqueue(new Point(e.X, e.Y - 1));
                    if ((e.Y < height - 1) && GetPixel(pixels, width, e.X, e.Y + 1) != replacementColor)
                        q.Enqueue(new Point(e.X, e.Y + 1));
                    e.X++;
                }
            }
        }

        private static Color GetPixel(Color[] pixels, int width, double x, double y)
        {
            int current = (int)(x + width * y);
            pixels[current].R = pixels[current].R >= 48 ? byte.MaxValue : byte.MinValue;
            pixels[current].G = pixels[current].G >= 48 ? byte.MaxValue : byte.MinValue;
            pixels[current].B = pixels[current].B >= 48 ? byte.MaxValue : byte.MinValue;
            pixels[current].A = byte.MaxValue;

            return pixels[current];
        }
    }
}
