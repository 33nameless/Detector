using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using ObjectDetectTrackApp.Models;

namespace ObjectDetectTrackApp.Services
{
    /// <summary>
    /// ONNX-детектор YOLOv5/v8/v11 (COCO). Отличия от прошлой версии:
    ///  - размер входа берётся из метаданных модели (было жёстко 416);
    ///  - letterbox-скейлинг вместо «растягивания» кадра (иначе боксы съезжают);
    ///  - формат выходного тензора определяется автоматически (v8 [1,4+nc,N],
    ///    v5 [1,N,5+nc], end2end [1,300,6]); старый декодер YOLOv2 удалён —
    ///    именно он рисовал «месиво» из боксов для современных моделей;
    ///  - координаты возвращаются в пикселях ИСХОДНОГО кадра;
    ///  - буферы переиспользуются между кадрами (нет аллокаций в потоке);
    ///  - лог в UI пишется не на каждый кадр, а раз в ~30 кадров.
    /// </summary>
    public class DetectorService : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly LogService _logService;
        private readonly string _inputName;

        private int _netW = 640;
        private int _netH = 640;

        // переиспользуемые буферы покадровой обработки
        private Mat _resized = new Mat();
        private Mat _padded = new Mat();
        private Vec3b[] _pixels = Array.Empty<Vec3b>();
        private float[]? _inputBuf;
        private DenseTensor<float>? _tensor;

        private YoloOutputFormat _outFormat = YoloOutputFormat.Unsupported;
        private bool _firstRunDone = false;
        // seg-ветка: второй выход [1,32,mh,mw] (proto-базис)
        private bool _isSeg;
        private int _segClasses;
        private readonly string _protoName = null;
        /// <summary>Строить ли полигоны масок в Detect() (включает VM при режимах Контур/Маска).</summary>
        public bool BuildMasks { get; set; }
        public bool IsSegModel => _isSeg;
        private int _frameCounter = 0;

        public event Action<string> OnError;

        public DetectorService(string modelPath, LogService logService)
        {
            _logService = logService;

            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Файл модели не найден", modelPath);

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) // все ядра на CPU-инференс
            };
            _session = new InferenceSession(modelPath, options);
            _inputName = _session.InputMetadata.Keys.First();

            // 1) размер входа из метаданных сессии (если модель не динамическая)
            var inDims = _session.InputMetadata[_inputName].Dimensions;
            if (inDims.Length == 4 && inDims[2] > 0 && inDims[3] > 0)
            {
                _netH = inDims[2];
                _netW = inDims[3];
            }
            else
            {
                _logService?.Info("Модель с динамическим размером входа — использую 640×640.");
            }
            _logService?.Info($"Вход модели: {_netW}x{_netH}.");

            // 2) предсказание формата выхода (для лога и честной ошибки, если формат чужой)
            var outEntry = _session.OutputMetadata.FirstOrDefault();
            var outDims = outEntry.Value?.Dimensions is int[] d ? d.ToArray() : null;
            _outFormat = YoloPostprocess.DetectFormat(outDims);
            string shape = outDims != null ? "[" + string.Join(",", outDims) + "]" : "?";
            switch (_outFormat)
            {
                case YoloOutputFormat.Transposed: _logService?.Info($"Выход {shape} — YOLOv8/v11 (transpose)."); break;
                case YoloOutputFormat.RowMajor:   _logService?.Info($"Выход {shape} — YOLOv5 (anchors, row-major)."); break;
                case YoloOutputFormat.End2End:    _logService?.Info($"Выход {shape} — end-to-end (NMS внутри модели)."); break;
                default:
                    _logService?.Error($"Неизвестный формат выхода модели ({shape}). Поддерживаются YOLOv5/v8/v11-экспорты ONNX (а также экспорт с nms=True). YOLOv2 [1,425,13,13] больше НЕ поддерживается — пересоберите модель: yolo export model=yolov8n.onnx-compatible вариантом из ultralytics.");
                    break;
            }

            // 3) есть ли seg-ветка: второй выход вида [1,32,mh,mw]
            foreach (var kv in _session.OutputMetadata)
            {
                var dm = kv.Value?.Dimensions;
                if (dm != null && dm.Length == 4 && dm[1] == 32)
                {
                    _isSeg = true; _protoName = kv.Key;
                    if (outDims != null && outDims.Length == 3 && outDims[1] == 4 + 32 + 32) _segClasses = 80;
                    else if (outDims != null && outDims.Length == 3) _segClasses = outDims[1] - 36; // 4+nc+32
                    break;
                }
            }
            if (_isSeg) _logService?.Info($"Сегментационная модель: выход [{_segClasses + 36} x ...] + proto {_protoName}.");

            _inputBuf = new float[3 * _netW * _netH];
            _tensor = new DenseTensor<float>(_inputBuf, new[] { 1, 3, _netH, _netW });
        }

        /// <summary>
        /// Детекция на кадре. Ожидается кадр в BGR (как в FrameData.Frame — нативный
        /// OpenCV); конверсия BGR2RGB для сети выполняется внутри.
        /// Возвращает боксы в координатах переданного кадра.
        /// </summary>
        public List<Detection> Detect(Mat frame, float confidenceThreshold = 0.45f, float nmsThreshold = 0.45f, bool inputIsRgb = false)
        {
            if (frame == null || frame.Empty())
                return new List<Detection>();

            if (_outFormat == YoloOutputFormat.Unsupported)
            {
                ReportOnce("Формат выхода модели не распознан — детекция пропущена.");
                return new List<Detection>();
            }

            try
            {
                // --- Letterbox-препроцессing (как в ultralytics): масштаб + серые поля 114 ---
                int w = frame.Width, h = frame.Height;
                float scale = Math.Min((float)_netW / w, (float)_netH / h);
                int nw = Math.Max(1, (int)Math.Round(w * scale));
                int nh = Math.Max(1, (int)Math.Round(h * scale));
                int padX = (_netW - nw) / 2;
                int padY = (_netH - nh) / 2;

                Cv2.Resize(frame, _resized, new Size(nw, nh), interpolation: InterpolationFlags.Linear);
                if (padX == 0 && padY == 0)
                {
                    _resized.CopyTo(_padded);
                }
                else
                {
                    Cv2.CopyMakeBorder(_resized, _padded, padY, _netH - nh - padY, padX, _netW - nw - padX,
                                       BorderTypes.Constant, new Scalar(114, 114, 114));
                }

                Mat rgbSrc = _padded;
                Mat converted = null;
                if (!inputIsRgb)
                {
                    converted = new Mat();
                    Cv2.CvtColor(_padded, converted, ColorConversionCodes.BGR2RGB);
                    rgbSrc = converted;
                }

                try
                {
                    FillTensor(rgbSrc);

                    float[] output;
                    int[] dims;
                    float[] proto = null; int[] protoDims = null;
                    using (var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, _tensor) }))
                    using (var en = results.GetEnumerator())
                    {
                        en.MoveNext();
                        var t = en.Current.AsTensor<float>();
                        dims = t.Dimensions.ToArray();
                        output = t is DenseTensor<float> dense ? dense.ToArray() : t.ToArray();

                        if (_isSeg && BuildMasks && en.MoveNext())
                        {
                            var pt = en.Current.AsTensor<float>();
                            protoDims = pt.Dimensions.ToArray();
                            proto = pt is DenseTensor<float> pd ? pd.ToArray() : pt.ToArray();
                        }
                    }

                    var lb = new YoloPostprocess.Letterbox(scale, padX, padY);
                    var dets = YoloPostprocess.Decode(output, dims, w, h, lb,
                                                      confidenceThreshold, nmsThreshold, CocoClasses.GetClassName,
                                                      _isSeg ? _segClasses : 0);

                    // полигоны масок: mask 160 -> x4 линейно -> affine «анлиттербокс» в координаты кадра -> контуры
                    if (_isSeg && BuildMasks && proto != null && dets.Count > 0)
                    {
                        try { AttachMaskPolygons(output, dims, proto, protoDims, dets, scale, padX, padY, w, h); }
                        catch (Exception mex) { _logService?.Warn("Маски пропущены: " + mex.Message); }
                    }

                    LogPeriodic(dets.Count, output.Length);
                    return dets;
                }
                finally
                {
                    converted?.Dispose();
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke("Ошибка инференса: " + ex.Message);
                _logService?.Error("Ошибка инференса: " + ex.Message);
                return new List<Detection>();
            }
        }

        private void AttachMaskPolygons(float[] out0, int[] dims0, float[] proto, int[] protoDims,
                                        List<Detection> dets, float scale, int padX, int padY, int fw, int fh)
        {
            int mh = protoDims[2], mw = protoDims[3];
            // ВНИМАНИЕ: у фиксированных экспортов вход НЕ квадратный (например 640x480).
            // Все отображения маски — в РАЗМЕР РЕАЛЬНОГО входа, иначе маска растягивается
            // по вертикали и «уезжает» вниз тем сильнее, чем ниже объект в кадре.
            int netSize = _netW;
            int netH = _netH;
            if (netH % mh != 0 || netSize % mw != 0) { netH = mh * Math.Max(1, netSize / mw); } // страховка
            // affine: x_frame = (x_net - padX)/scale  →  матрица 2x3 для WarpAffine
            using Mat m23 = new Mat(2, 3, MatType.CV_64FC1);
            double[] a = { 1.0 / scale, 0, -padX / (double)scale, 0, 1.0 / scale, -padY / (double)scale };
            for (int r0 = 0; r0 < 2; r0++)
                for (int c0 = 0; c0 < 3; c0++)
                    m23.Set<double>(r0, c0, a[r0 * 3 + c0]);

            using Mat frameMask = new Mat();
            foreach (var d in dets)
            {
                if (d.AnchorIdx < 0) continue;
                int nx0 = (int)Math.Max(0, Math.Floor(d.X * scale)) + padX;
                int ny0 = (int)Math.Max(0, Math.Floor(d.Y * scale)) + padY;
                int nx1 = Math.Min(netSize, (int)Math.Ceiling((d.X + d.Width) * scale)) + padX;
                int ny1 = Math.Min(netH, (int)Math.Ceiling((d.Y + d.Height) * scale)) + padY;

                float[] mask = YoloPostprocess.BuildInstanceMask(out0, dims0, proto, protoDims,
                                                                  d.AnchorIdx, netSize,
                                                                  new YoloPostprocess.RectI(nx0, ny0, nx1, ny1));
                if (mask.Length == 0) continue;

                // ВАЖНО: resize/convertTo только в ДРУГОЙ Mat (in-place со сменой размера/типа
                // в OpenCV запрещён — маски «съезжали» с объектов).
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(mask, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    using Mat m160 = new Mat(mh, mw, MatType.CV_32FC1, handle.AddrOfPinnedObject());
                    using Mat m640 = new Mat();
                    Cv2.Resize(m160, m640, new Size(netSize, netH), 0, 0, InterpolationFlags.Linear);
                    Cv2.Threshold(m640, m640, 0.5, 255.0, ThresholdTypes.Binary);
                    using Mat m8 = new Mat();
                    m640.ConvertTo(m8, MatType.CV_8UC1); // в отдельный Mat, тип -> byte
                    // перенос маски из пространства letterbox в кадр (аффинирует в новый Mat)
                    Cv2.WarpAffine(m8, frameMask, m23, new Size(fw, fh),
                                   InterpolationFlags.Nearest, BorderTypes.Constant, new Scalar(0));
                }
                finally { handle.Free(); }

                Cv2.FindContours(frameMask, out Point[][] contours, out _,
                                 RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                if (contours.Length == 0) continue;
                var big = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
                big = Cv2.ApproxPolyDP(big, 2.5, true);
                if (big.Length >= 3)
                {
                    var poly = new float[big.Length * 2];
                    for (int i = 0; i < big.Length; i++) { poly[2 * i] = big[i].X; poly[2 * i + 1] = big[i].Y; }
                    d.MaskPolygon = poly;
                }
            }
        }

        /// <summary>RGB Mat [H,W,3] 0..255 -> float CHW 0..1 без LINQ на пиксель.</summary>
        private void FillTensor(Mat rgb)
        {
            rgb.GetArray(out _pixels); // Vec3b[] — ровно netW*netH элементов (после padding)
            float[] buf = _inputBuf!;
            int plane = _netW * _netH;
            for (int i = 0; i < plane; i++)
            {
                Vec3b v = _pixels[i];      // каналы уже в порядке R,G,B
                buf[i] = v.Item0 / 255f;
                buf[plane + i] = v.Item1 / 255f;
                buf[2 * plane + i] = v.Item2 / 255f;
            }
        }

        private void LogPeriodic(int detCount, int outLen)
        {
            _frameCounter++;
            if (!_firstRunDone)
            {
                _firstRunDone = true;
                _logService?.Info($"Первый проход OK: боксов {detCount}, выходных значений {outLen}.");
            }
            else if (_frameCounter % 30 == 0)
            {
                _logService?.Info($"Кадр {_frameCounter}: боксов {detCount}."); // не спамим в UI каждый кадр
            }
        }

        private string _lastReported = null;
        private void ReportOnce(string msg)
        {
            if (_lastReported == msg) return;
            _lastReported = msg;
            OnError?.Invoke(msg);
            _logService?.Warn(msg);
        }

        public void Dispose()
        {
            _session?.Dispose();
            _resized?.Dispose();
            _padded?.Dispose();
        }
    }
}
