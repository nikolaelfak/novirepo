using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.ColorSpaces.Conversion;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace ImageToGif
{
    public class HttpContextTimer
    {
        public HttpListenerContext Context { get; set; }
        public Stopwatch Timer { get; set; }
    }

    class ItG
    {
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();
        private const string CacheFolderPath = "../../../KesFolder";
        private const string ImagesFolderPath = "../../../Slike";

        private static object locker = new object();
        private const int NumOfFrames = 30;
        private const int FrameDelay = 10;

        private static void ShowLoading()
        {
            Console.Write("Processing");
            for (int i = 0; i < 10; i++)
            {
                Console.Write(".");
                System.Threading.Thread.Sleep(200);
            }

            Console.WriteLine();
        }

        private static void LoadCacheFolder()
        {
            if (!Directory.Exists(CacheFolderPath))
            {
                Directory.CreateDirectory(CacheFolderPath);
                Console.WriteLine("Kes folder je uspesno kreiran.");
            }
            else
            {
                foreach (string imgPath in Directory.GetFiles(CacheFolderPath))
                {
                    string filename = Path.GetFileName(imgPath);
                    byte[] image_data = File.ReadAllBytes(imgPath);
                    Cache[filename] = image_data;
                }
                Console.WriteLine("Kes memorija je uspesno ucitana!");
            }
        }

        private static byte[] ReadCache(string filename)
        {
            Console.WriteLine("Ucitavanje slike iz kes memorije...");
            return Cache[filename];
        }

        private static void WriteCache(string original, string filename, byte[] image_data)
        {
            Console.WriteLine("Upisivanje slike u kes memoriju...");
            Cache[filename] = image_data;
        }

        private static void ProcessRequest(HttpContextTimer httpContextTimer)
        {
            if (!ThreadPool.QueueUserWorkItem(ProcessRequestExecute, httpContextTimer))
            {
                httpContextTimer.Context.Response.StatusCode = 500;
                HttpResponse("500 - Connection Failed", null, httpContextTimer);
            }
        }

        private static void ProcessRequestExecute(object state)
        {
            HttpContextTimer httpContextTimer = (HttpContextTimer)state;

            HttpListenerContext context = httpContextTimer.Context;
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            Console.WriteLine($"Request received:\n" +
                $"Hostname: {request.UserHostName}\n" +
                $"HTTP method: {request.HttpMethod}\n" +
                $"HTTP headers: {request.Headers}\n" +
                $"Content type: {request.ContentType}\n" +
                $"Content length: {request.ContentLength64}\n");

            string query = request.Url.AbsolutePath;
            string filename = query[1..];

            if (query == "imgCache")
            {
                response.StatusCode = 403;
                HttpResponse("403 - Access Denied", null, httpContextTimer);
                return;
            }

            if (query == "/")
            {
                response.StatusCode = 404;
                HttpResponse("404 - Not Found", null, httpContextTimer);
                return;
            }

            if (Cache.ContainsKey(filename))
            {
                Console.WriteLine("Slika je pronadnjena u kesu...");
                byte[] res_data = ReadCache(filename);
                HttpResponse(filename, res_data, httpContextTimer);
            }
            else
            {
                string path = Path.Combine(ImagesFolderPath, filename);
                byte[] gif_data = ConvertImageToGif(path, filename);
                ShowLoading();
                HttpResponse("200", gif_data, httpContextTimer);
            }
        }

        private static void HttpResponse(string responseString, byte[] res_data, HttpContextTimer httpContextTimer)
        {
            HttpListenerResponse res = httpContextTimer.Context.Response;

            byte[] buffer;
            if (res_data != null)
            {
                buffer = res_data;
                res.ContentLength64 = res_data.Length;
            }
            else
            {
                buffer = Encoding.UTF8.GetBytes(responseString);
                res.ContentLength64 = buffer.Length;
            }

            res.ContentType = "image/gif";
            res.OutputStream.Write(buffer, 0, buffer.Length);

            httpContextTimer.Timer.Stop();
            Console.WriteLine($"Response:\n" +
                $"Status code: {res.StatusCode}\n" +
                $"Content type: {res.ContentType}\n" +
                $"Content length: {res.ContentLength64}\n" +
                $"Time taken for response: {httpContextTimer.Timer.ElapsedMilliseconds} ms\n" +
                $"Body: {responseString}\n");
        }

        private static byte[] CompressImage(string path)
        {
            using (var image = Image.Load<Rgba32>(path))
            {
                image.Mutate(x => x.AutoOrient());
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(800, 600),
                    Mode = ResizeMode.Max
                }));

                var encoder = new JpegEncoder
                {
                    Quality = 80
                };

                using (var memoryStream = new MemoryStream())
                {
                    image.Save(memoryStream, encoder);
                    return memoryStream.ToArray();
                }
            }
        }

        private static byte[] ConvertImageToGif(string path, string filename)
        {
            string gifFilename = filename.Replace(".png", ".gif");

            try
            {
                var pngImage = Image.Load<Rgba32>(path);
                int width = pngImage.Width;
                int height = pngImage.Height;

                var gifImage = new Image<Rgba32>(width, height);

                for (int i = 0; i < NumOfFrames; i++)
                {
                    var clone = pngImage.Clone();
                    if (i % 2 == 0)
                    {
                        clone.Mutate(x => x.Grayscale());
                    }
                    if (i % 3 == 0)
                    {
                        clone.Mutate(x => x.ColorBlindness(ColorBlindnessMode.Deuteranopia));
                    }
                    if (i % 5 == 0)
                    {
                        clone.Mutate(x => x.ColorBlindness(ColorBlindnessMode.Tritanopia));
                    }
                    if (i % 9 == 0)
                    {
                        clone.Mutate(x => x.Grayscale());
                    }
                    gifImage.Frames.AddFrame(clone.Frames[0]);
                    gifImage.Frames[gifImage.Frames.Count - 1].Metadata.GetGifMetadata().FrameDelay = FrameDelay;
                }

                string gifPath = Path.Combine(CacheFolderPath, gifFilename);

                Console.WriteLine("Ceka se na upis fajla u kes folder...");
                lock (locker)
                {
                    using (FileStream stream = new FileStream(gifPath, FileMode.Create))
                    {
                        gifImage.SaveAsGif(stream, new GifEncoder { ColorTableMode = GifColorTableMode.Local });
                        Console.WriteLine("Gif slika je uspesno upisana u kes fodler!");
                    }
                }

                byte[] compressedImageData = CompressImage(gifPath);

                WriteCache(filename, gifFilename, File.ReadAllBytes(gifPath));

                return File.ReadAllBytes(gifPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("An error occurred during image to gif conversion: " + ex.Message);
                return null;
            }
        }

        static void Main(string[] args)
        {
            Console.BackgroundColor = ConsoleColor.White;
            Console.ForegroundColor = ConsoleColor.Black;
            Console.Clear();
            Console.WriteLine("\t\t\t\t\t\tIMAGE_TO_GIF_CONVERTER");
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:5050/");
            listener.Start();
            LoadCacheFolder();
            Console.WriteLine("Web server je pokrenut na portu 5050\n\n");

            do
            {
                HttpListenerContext context = listener.GetContext();
                HttpContextTimer httpContextTimer = new HttpContextTimer();

                httpContextTimer.Context = context;
                httpContextTimer.Timer = new Stopwatch();
                httpContextTimer.Timer.Start();

                ProcessRequest(httpContextTimer);
            } while (listener.IsListening);
            listener.Stop();
        }
    }
}
