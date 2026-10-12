using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ObjectDetectTrackApp.Models;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ObjectDetectTrackApp.Services
{
    /// <summary>
    /// Детектор объектов на основе TinyYolo2 ONNX модели
    /// </summary>
    public class DetectorService
    {
        private InferenceSession _session;
        private readonly LogService _logService;
        private const int InputSize = 416;

        // PASCAL VOC 20 классов (TinyYolo2)
        private readonly string[] _pascalVocClasses = new[]
        {
            "aeroplane", "bicycle", "bird", "boat", "bottle", "bus", "car", "cat",
            "chair", "cow", "diningtable", "dog", "horse", "motorbike", "person",
            "pottedplant", "sheep", "sofa", "train", "tvmonitor"
        };

        public DetectorService(string modelPath, LogService logService)
        {
            _logService = logService;

            try
            {
                _logService.Info($"Loading TinyYolo2 model from: {modelPath}");

                if (!System.IO.File.Exists(modelPath))
                {
                    _logService.Error($"Model file not found: {modelPath}");
                    throw new FileNotFoundException($"Model not found at: {modelPath}");
                }

                // Создаём ONNX Runtime сессию
                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };

                _session = new InferenceSession(modelPath, sessionOptions);

                _logService.Info("✓ TinyYolo2 ONNX model loaded successfully");
                _logService.Info($"  Input names: {string.Join(", ", _session.InputNames)}");
                _logService.Info($"  Output names: {string.Join(", ", _session.OutputNames)}");
            }
            catch (Exception ex)
            {
                _logService.Error($"Failed to load model: {ex.Message}");
                _logService.Error($"  Stack: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Выполнить детекцию на кадре
        /// </summary>
        public List<Detection> Detect(Mat image, float confidenceThreshold = 0.45f)
        {
            if (_session == null)
            {
                _logService.Error("Session is null - model not loaded");
                return new List<Detection>();
            }

            if (image == null || image.Empty())
            {
                _logService.Error("Input image is null or empty");
                return new List<Detection>();
            }

            try
            {
                _logService.Info($"[Detect] Processing {image.Width}x{image.Height} image...");

                // Resize к 416x416
                Mat resized = new Mat();
                Cv2.Resize(image, resized, new Size(InputSize, InputSize),
                    interpolation: InterpolationFlags.Linear);

                // Преобразуем в tensor
                var tensor = MatToTensor(resized);

                // Запускаем inference
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_session.InputNames[0], tensor)
                };

                using (var results = _session.Run(inputs))
                {
                    var outputName = _session.OutputNames[0];
                    var outputTensor = results.First(r => r.Name == outputName).Value as DenseTensor<float>;

                    if (outputTensor == null)
                    {
                        _logService.Error("Output tensor is null");
                        resized.Dispose();
                        return new List<Detection>();
                    }

                    var outputArray = outputTensor.ToArray();
                    _logService.Info($"[Detect] Output shape: {string.Join("x", outputTensor.Dimensions)}");
                    _logService.Info($"[Detect] Output length: {outputArray.Length}");

                    var detections = PostprocessOutput(outputArray, image.Width, image.Height, confidenceThreshold);
                    _logService.Info($"[Detect] ✓ Found {detections.Count} detections");

                    resized.Dispose();
                    return detections;
                }
            }
            catch (Exception ex)
            {
                _logService.Error($"Detection error: {ex.Message}");
                _logService.Error($"  Stack: {ex.StackTrace}");
                return new List<Detection>();
            }
        }

        /// <summary>
        /// Преобразовать Mat кадр в ONNX tensor [1, 3, 416, 416]
        /// </summary>
        private DenseTensor<float> MatToTensor(Mat mat)
        {
            try
            {
                // BGR -> RGB
                Mat rgb = new Mat();
                if (mat.Channels() == 3)
                {
                    Cv2.CvtColor(mat, rgb, ColorConversionCodes.BGR2RGB);
                }
                else if (mat.Channels() == 1)
                {
                    Cv2.CvtColor(mat, rgb, ColorConversionCodes.GRAY2RGB);
                }
                else
                {
                    rgb = mat.Clone();
                }

                // Создаём массив [1 * 3 * 416 * 416]
                float[] data = new float[1 * 3 * InputSize * InputSize];
                int idx = 0;

                // NCHW формат: каналы идут первыми
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < InputSize; y++)
                    {
                        for (int x = 0; x < InputSize; x++)
                        {
                            byte pixel = rgb.At<Vec3b>(y, x)[c];
                            data[idx++] = pixel / 255f; // Нормализуем [0, 1]
                        }
                    }
                }

                rgb.Dispose();

                var tensor = new DenseTensor<float>(data, new[] { 1, 3, InputSize, InputSize });
                return tensor;
            }
            catch (Exception ex)
            {
                _logService.Error($"MatToTensor error: {ex.Message}");
                // Возвращаем нулевой tensor
                var zeros = new float[1 * 3 * InputSize * InputSize];
                return new DenseTensor<float>(zeros, new[] { 1, 3, InputSize, InputSize });
            }
        }

        /// <summary>
        /// Постобработка: парсим выход сети и применяем NMS
        /// </summary>
        private List<Detection> PostprocessOutput(float[] output, int origW, int origH, float confidenceThreshold)
        {
            try
            {
                var detections = new List<Detection>();

                if (output == null || output.Length == 0)
                {
                    _logService.Warn("Empty output");
                    return detections;
                }

                // TinyYolo2: [1, 425, 13, 13] -> flatten [425*13*13 = 71825]
                // 425 = (20 classes + 4 coords + 1 conf) * 5 anchors
                int gridSize = 13;
                int numAnchors = 5;
                int numClasses = 20;
                int predPerCell = (numClasses + 5) * numAnchors; // 125

                _logService.Info($"[PostProc] Grid: {gridSize}x{gridSize}, Anchors: {numAnchors}, Classes: {numClasses}");

                int detCount = 0;

                for (int y = 0; y < gridSize; y++)
                {
                    for (int x = 0; x < gridSize; x++)
                    {
                        for (int a = 0; a < numAnchors; a++)
                        {
                            int baseIdx = y * gridSize * predPerCell + x * predPerCell + a * (numClasses + 5);

                            if (baseIdx + 4 >= output.Length)
                                continue;

                            // Достаём objectness confidence
                            float objectness = Sigmoid(output[baseIdx + 4]);

                            if (objectness < confidenceThreshold)
                                continue;

                            // Координаты бокса (в координатах сетки)
                            float tx = Sigmoid(output[baseIdx + 0]);
                            float ty = Sigmoid(output[baseIdx + 1]);
                            float tw = (float)Math.Exp(output[baseIdx + 2]);
                            float th = (float)Math.Exp(output[baseIdx + 3]);

                            // Абсолютные координаты в оригинальном изображении
                            float cx = (tx + x) / gridSize * origW;
                            float cy = (ty + y) / gridSize * origH;
                            float w = tw * InputSize / gridSize;
                            float h = th * InputSize / gridSize;

                            // Лучший класс
                            float maxClassProb = 0;
                            int classId = 0;

                            if (baseIdx + 5 + numClasses <= output.Length)
                            {
                                for (int c = 0; c < numClasses; c++)
                                {
                                    float classProb = Sigmoid(output[baseIdx + 5 + c]);
                                    if (classProb > maxClassProb)
                                    {
                                        maxClassProb = classProb;
                                        classId = c;
                                    }
                                }
                            }

                            float finalConf = objectness * maxClassProb;

                            if (finalConf < confidenceThreshold)
                                continue;

                            if (classId < 0 || classId >= _pascalVocClasses.Length)
                                continue;

                            // Координаты в пикселях
                            int px = (int)(cx - w / 2);
                            int py = (int)(cy - h / 2);
                            int pw = (int)w;
                            int ph = (int)h;

                            // Clip к границам
                            px = Math.Max(0, px);
                            py = Math.Max(0, py);
                            pw = Math.Min(origW - px, pw);
                            ph = Math.Min(origH - py, ph);

                            if (pw > 0 && ph > 0)
                            {
                                detections.Add(new Detection
                                {
                                    ClassName = _pascalVocClasses[classId],
                                    Confidence = finalConf,
                                    X = px,
                                    Y = py,
                                    Width = pw,
                                    Height = ph,
                                    Timestamp = DateTime.Now
                                });

                                detCount++;
                            }
                        }
                    }
                }

                _logService.Info($"[PostProc] Before NMS: {detCount} detections");

                if (detections.Count > 0)
                {
                    var classDistribution = detections
                        .GroupBy(d => d.ClassName)
                        .Select(g => $"{g.Key}({g.Count()})")
                        .ToList();

                    _logService.Info($"[PostProc] Classes: {string.Join(", ", classDistribution)}");
                }

                return ApplyNms(detections, 0.5f);
            }
            catch (Exception ex)
            {
                _logService.Error($"Postprocessing error: {ex.Message}");
                _logService.Error($"  Stack: {ex.StackTrace}");
                return new List<Detection>();
            }
        }

        /// <summary>
        /// Sigmoid функция (с защитой от overflow)
        /// </summary>
        private float Sigmoid(float x)
        {
            x = Math.Min(Math.Max(x, -500), 500);
            return 1f / (1f + (float)Math.Exp(-x));
        }

        /// <summary>
        /// Non-Maximum Suppression
        /// </summary>
        private List<Detection> ApplyNms(List<Detection> boxes, float nmsThreshold)
        {
            if (boxes.Count == 0)
                return boxes;

            var sorted = boxes.OrderByDescending(b => b.Confidence).ToList();
            var result = new List<Detection>();

            foreach (var box in sorted)
            {
                bool keep = true;
                foreach (var kept in result)
                {
                    if (CalcIou(box, kept) > nmsThreshold)
                    {
                        keep = false;
                        break;
                    }
                }
                if (keep) result.Add(box);
            }

            _logService.Info($"[NMS] {boxes.Count} -> {result.Count}");
            return result;
        }

        /// <summary>
        /// Intersection over Union
        /// </summary>
        private float CalcIou(Detection a, Detection b)
        {
            int xi1 = Math.Max(a.X, b.X);
            int yi1 = Math.Max(a.Y, b.Y);
            int xi2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int yi2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            int inter = Math.Max(0, xi2 - xi1) * Math.Max(0, yi2 - yi1);
            int uni = a.Width * a.Height + b.Width * b.Height - inter;

            return uni > 0 ? (float)inter / uni : 0;
        }

        public void Dispose()
        {
            try
            {
                _session?.Dispose();
                _logService.Info("Detector disposed");
            }
            catch { }
        }
    }
}