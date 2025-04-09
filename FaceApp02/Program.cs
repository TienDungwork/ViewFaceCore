using System;
using System.Text;
using ViewFaceCore.Core;
using SkiaSharp;
using System.IO;
using System.Diagnostics;
using ViewFaceCore.Model;
using SocketIOClient;
using System.Threading.Tasks;
using ViewFaceCore;

namespace FaceAntiSpoofingServer
{
    class Program
    {
        static SocketIOClient.SocketIO? client;
        static byte[]? referenceImageBytes = null;
        static float[]? referenceFeatures = null;
        static readonly byte[] referenceHeader = Encoding.UTF8.GetBytes("REF_");
        static int frameCounter = 0;
        static bool isSpoofChecking = false;
        static DateTime spoofCheckStart = DateTime.MinValue;
        static bool expectingImageAfterHeader = false;
        static bool referenceProcessed = false;
        static readonly object frameLock = new object();

        static FaceDetector faceDetector = new FaceDetector();
        static FaceLandmarker faceLandmarker = new FaceLandmarker();
        static FaceAntiSpoofing faceAntiSpoofing = new FaceAntiSpoofing();
        static FaceRecognizer faceRecognizer = new FaceRecognizer();

        static async Task Main(string[] args)
        {
            Environment.SetEnvironmentVariable("OMP_NUM_THREADS", "2");
            Console.WriteLine("Phần mềm 02: Đang khởi động...");
            ResetState();

            string serverUrl = "http://192.168.1.98:6590";
            client = new SocketIOClient.SocketIO(serverUrl, new SocketIOOptions
            {
                Reconnection = false,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
            });

            SetupSocketIO();

            try
            {
                await client.ConnectAsync();
                Console.WriteLine("done");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi khi kết nối tới server: {ex.Message}");
                return;
            }
            //Console.WriteLine("Client đang chạy... Nhấn Ctrl+C để thoát");
            Console.ReadLine();
            await client.DisconnectAsync();
        }

        static void SetupSocketIO()
        {
            if (client == null) return;

            client.OnConnected += async (sender, e) => { Console.WriteLine("Đã kết nối tới server thành công"); await Task.CompletedTask; };
            client.OnDisconnected += (sender, e) => { Console.WriteLine("Ngắt kết nối từ server"); ResetState(); };
            client.On("frame", response =>
            {
                byte[] frameBytes = response.GetValue<byte[]>();
                ProcessFrame(frameBytes);
            });
            client.OnError += (sender, e) => Console.WriteLine($"Lỗi kết nối: {e}");
        }

        static void ProcessFrame(byte[] frameBytes)
        {
            lock (frameLock)
            {
                if (isSpoofChecking && (DateTime.Now - spoofCheckStart).TotalSeconds < 0.1) return;
                spoofCheckStart = DateTime.Now;

                Stopwatch sw = Stopwatch.StartNew();

                // Nhận header REF_
                if (frameBytes.Length == 4 && StartsWith(frameBytes, referenceHeader))
                {
                    Console.WriteLine("Đã nhận header REF_, bắt đầu chu kỳ mới...");
                    ResetState();
                    expectingImageAfterHeader = true;
                    return;
                }

                // Chỉ xử lý ảnh tham chiếu đầu tiên sau header REF_
                if (expectingImageAfterHeader && !referenceProcessed)
                {
                    if (frameBytes.Length < 100) // Bỏ qua frame không hợp lệ
                    {
                        Console.WriteLine("Frame không hợp lệ sau header REF_, bỏ qua...");
                        return;
                    }

                    Console.WriteLine($"Đã nhận ảnh tham chiếu mới, kích thước: {frameBytes.Length} bytes");
                    referenceImageBytes = frameBytes;
                    referenceProcessed = true;
                    expectingImageAfterHeader = false;

                    sw.Restart();
                    SKBitmap? referenceBitmap = ByteArrayToSKBitmap(referenceImageBytes);
                    long refDecodeTime = sw.ElapsedMilliseconds;
                    if (referenceBitmap == null)
                    {
                        _ = SendResultAsync("Ảnh tham chiếu không hợp lệ.");
                        Console.WriteLine($"RefDecode: {refDecodeTime}ms");
                        ResetState();
                        return;
                    }

                    sw.Restart();
                    var refFaceInfos = faceDetector.Detect(referenceBitmap);
                    long refDetectTime = sw.ElapsedMilliseconds;
                    if (refFaceInfos == null || refFaceInfos.Length == 0)
                    {
                        _ = SendResultAsync("Ảnh tham chiếu không có khuôn mặt.");
                        referenceBitmap.Dispose();
                        Console.WriteLine($"RefDetect: {refDetectTime}ms");
                        ResetState();
                        return;
                    }

                    sw.Restart();
                    var refPoints = faceLandmarker.Mark(referenceBitmap, refFaceInfos[0]);
                    long refMarkTime = sw.ElapsedMilliseconds;

                    sw.Restart();
                    referenceFeatures = faceRecognizer.Extract(referenceBitmap, refPoints);
                    long refExtractTime = sw.ElapsedMilliseconds;

                    Console.WriteLine($"Processed Reference - RefDecode: {refDecodeTime}ms, RefDetect: {refDetectTime}ms, RefMark: {refMarkTime}ms, RefExtract: {refExtractTime}ms");
                    referenceBitmap.Dispose();

                    isSpoofChecking = true;
                    spoofCheckStart = DateTime.Now;
                    Console.WriteLine("Bắt đầu chu kỳ nhận diện.");
                    return;
                }

                // Bỏ qua frame nếu không trong giai đoạn nhận diện hoặc đã xử lý ảnh tham chiếu
                if (!isSpoofChecking || referenceFeatures == null)
                {
                    Console.WriteLine($"Bỏ qua frame thừa: Kích thước {frameBytes.Length} bytes");
                    return;
                }

                // Xử lý frame webcam
                if (isSpoofChecking && (DateTime.Now - spoofCheckStart).TotalSeconds > 60)
                {
                    _ = SendResultAsync("Xác thực thất bại - Timeout");
                    Console.WriteLine("Xác thực thất bại");
                    ResetState();
                    return;
                }

                frameCounter++;
                if (frameCounter % 3 != 0) return;

                sw.Restart();
                SKBitmap? bitmap = ByteArrayToSKBitmap(frameBytes);
                long decodeTime = sw.ElapsedMilliseconds;
                if (bitmap == null)
                {
                    Console.WriteLine($"Frame không hợp lệ, bỏ qua... Decode: {decodeTime}ms");
                    return;
                }

                sw.Restart();
                var faceInfos = faceDetector.Detect(bitmap);
                long detectTime = sw.ElapsedMilliseconds;
                if (faceInfos == null || faceInfos.Length == 0)
                {
                    Console.WriteLine($"Không phát hiện khuôn mặt trong frame webcam, Detect: {detectTime}ms");
                    bitmap.Dispose();
                    return;
                }

                sw.Restart();
                var faceInfo = faceInfos[0];
                var points = faceLandmarker.Mark(bitmap, faceInfo);
                long markTime = sw.ElapsedMilliseconds;

                sw.Restart();
                var antiSpoofResult = faceAntiSpoofing.AntiSpoofing(bitmap, faceInfo, points);
                long antiSpoofTime = sw.ElapsedMilliseconds;
                if (antiSpoofResult.Status != AntiSpoofingStatus.Real)
                {
                    Console.WriteLine($"Frame giả mạo: Clarity: {antiSpoofResult.Clarity:F6} | Reality: {antiSpoofResult.Reality:F6}, AntiSpoof: {antiSpoofTime}ms");
                    bitmap.Dispose();
                    return;
                }

                sw.Restart();
                var features = faceRecognizer.Extract(bitmap, points);
                long extractTime = sw.ElapsedMilliseconds;

                sw.Restart();
                float similarity = CalculateSimilarity(features, referenceFeatures);
                long similarityTime = sw.ElapsedMilliseconds;

                Console.WriteLine($"Times - Decode: {decodeTime}ms, Detect: {detectTime}ms, Mark: {markTime}ms, AntiSpoof: {antiSpoofTime}ms, Extract: {extractTime}ms, Similarity: {similarityTime}ms");

                long processingTime = decodeTime + detectTime + markTime + antiSpoofTime + extractTime + similarityTime;

                if (similarity > 0.75f)
                {
                    string result = $"Xác thực thành công | Similarity: {similarity:F2} | Thời gian: {processingTime}ms";
                    _ = SendResultAsync(result);
                    ResetState();
                    bitmap.Dispose();
                    return;
                }

                bitmap.Dispose();
            }
        }

        static void ResetState()
        {
            isSpoofChecking = false;
            expectingImageAfterHeader = false;
            referenceProcessed = false;
            referenceImageBytes = null;
            referenceFeatures = null;
            frameCounter = 0;
            spoofCheckStart = DateTime.MinValue;
            //Console.WriteLine("Trạng thái đã được reset hoàn toàn, tài nguyên đã được giải phóng, sẵn sàng cho chu kỳ mới.");
            // GC.Collect(); // Tùy chọn nếu RAM tăng bất thường
            // GC.WaitForPendingFinalizers();
        }

        static bool StartsWith(byte[] array, byte[] prefix)
        {
            if (array.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (array[i] != prefix[i]) return false;
            }
            return true;
        }

        static async Task SendResultAsync(string result)
        {
            if (client != null)
            {
                await client.EmitAsync("result", result);
                Console.WriteLine($"Đã gửi kết quả: {result}");
            }
        }

        static SKBitmap? ByteArrayToSKBitmap(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 10) return null;
                using var ms = new MemoryStream(data);
                return SKBitmap.Decode(ms);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static float CalculateSimilarity(float[] features1, float[] features2)
        {
            if (features1.Length != features2.Length) return 0f;
            float dotProduct = 0, norm1 = 0, norm2 = 0;
            for (int i = 0; i < features1.Length; i++)
            {
                dotProduct += features1[i] * features2[i];
                norm1 += features1[i] * features1[i];
                norm2 += features2[i] * features2[i];
            }
            float similarity = dotProduct / (float)(Math.Sqrt(norm1) * Math.Sqrt(norm2));
            return float.IsNaN(similarity) ? 0f : similarity;
        }
    }
}