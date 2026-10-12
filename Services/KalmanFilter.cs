using System;

namespace ObjectDetectTrackApp.Services
{
    /// <summary>
    /// Простой Kalman Filter для отслеживания bbox
    /// State: [x, y, w, h, vx, vy, vw] (7D)
    /// </summary>
    public class KalmanFilter
    {
        private float[] _x; // Состояние [x, y, w, h, vx, vy, vw]
        private float[,] _P; // Ковариация
        private float[,] _F; // Матрица переходов
        private float[,] _H; // Матрица измерений
        private float[,] _Q; // Шум процесса
        private float[,] _R; // Шум измерения

        public KalmanFilter()
        {
            _x = new float[7];
            _P = new float[7, 7];
            _F = new float[7, 7];
            _H = new float[4, 7];
            _Q = new float[7, 7];
            _R = new float[4, 4];

            // Инициализация матрицы переходов (identity + velocity)
            for (int i = 0; i < 7; i++)
            {
                _F[i, i] = 1.0f;
                _P[i, i] = 10.0f;
            }

            // Матрица измерений (первые 4 значения видны)
            for (int i = 0; i < 4; i++)
            {
                _H[i, i] = 1.0f;
            }

            // Шум процесса
            for (int i = 0; i < 4; i++)
                _Q[i, i] = 1.0f;
            for (int i = 4; i < 7; i++)
                _Q[i, i] = 0.01f;

            // Шум измерения
            for (int i = 0; i < 4; i++)
                _R[i, i] = 10.0f;
        }

        public void Init(float x, float y, float w, float h)
        {
            _x[0] = x;
            _x[1] = y;
            _x[2] = w;
            _x[3] = h;
            _x[4] = 0; // vx
            _x[5] = 0; // vy
            _x[6] = 0; // vw
        }

        public void Predict()
        {
            // x = F * x
            var newX = MatrixVectorMult(_F, _x);
            _x = newX;

            // P = F * P * F^T + Q
            var FP = MatrixMult(_F, _P);
            var FPFt = MatrixMult(FP, Transpose(_F));
            _P = MatrixAdd(FPFt, _Q);
        }

        public void Update(float z_x, float z_y, float z_w, float z_h)
        {
            float[] z = new float[] { z_x, z_y, z_w, z_h };

            // y = z - H * x (остаток)
            var Hx = MatrixVectorMult(_H, _x);
            float[] y = new float[4];
            for (int i = 0; i < 4; i++)
                y[i] = z[i] - Hx[i];

            // S = H * P * H^T + R
            var HP = MatrixMult(_H, _P);
            var HPHt = MatrixMult(HP, Transpose(_H));
            var S = MatrixAdd(HPHt, _R);

            // K = P * H^T * S^-1 (Kalman gain)
            var PHt = MatrixMult(_P, Transpose(_H));
            var Sinv = MatrixInverse(S);
            var K = MatrixMult(PHt, Sinv);

            // x = x + K * y
            var Ky = MatrixVectorMult(K, y);
            for (int i = 0; i < 7; i++)
                _x[i] += Ky[i];

            // P = (I - K * H) * P
            var KH = MatrixMult(K, _H);
            var I_KH = MatrixSubtract(Identity(7), KH);
            _P = MatrixMult(I_KH, _P);
        }

        public (float x, float y, float w, float h) GetState()
        {
            return (_x[0], _x[1], _x[2], _x[3]);
        }

        // Вспомогательные методы матричной алгебры
        private float[] MatrixVectorMult(float[,] M, float[] v)
        {
            int rows = M.GetLength(0);
            int cols = M.GetLength(1);
            float[] result = new float[rows];
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                    result[i] += M[i, j] * v[j];
            }
            return result;
        }

        private float[,] MatrixMult(float[,] A, float[,] B)
        {
            int n = A.GetLength(0);
            int m = A.GetLength(1);
            int p = B.GetLength(1);
            float[,] result = new float[n, p];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < p; j++)
                {
                    for (int k = 0; k < m; k++)
                        result[i, j] += A[i, k] * B[k, j];
                }
            }
            return result;
        }

        private float[,] MatrixAdd(float[,] A, float[,] B)
        {
            int n = A.GetLength(0);
            int m = A.GetLength(1);
            float[,] result = new float[n, m];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < m; j++)
                    result[i, j] = A[i, j] + B[i, j];
            }
            return result;
        }

        private float[,] MatrixSubtract(float[,] A, float[,] B)
        {
            int n = A.GetLength(0);
            int m = A.GetLength(1);
            float[,] result = new float[n, m];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < m; j++)
                    result[i, j] = A[i, j] - B[i, j];
            }
            return result;
        }

        private float[,] Transpose(float[,] M)
        {
            int n = M.GetLength(0);
            int m = M.GetLength(1);
            float[,] result = new float[m, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < m; j++)
                    result[j, i] = M[i, j];
            }
            return result;
        }

        private float[,] Identity(int n)
        {
            float[,] I = new float[n, n];
            for (int i = 0; i < n; i++)
                I[i, i] = 1.0f;
            return I;
        }

        private float[,] MatrixInverse(float[,] M)
        {
            // Упрощённая инверсия для 4x4 матрицы
            // Используем Gaussian elimination
            int n = M.GetLength(0);
            float[,] A = (float[,])M.Clone();
            float[,] I = Identity(n);

            for (int col = 0; col < n; col++)
            {
                // Найти pivot
                int pivotRow = col;
                for (int row = col + 1; row < n; row++)
                {
                    if (Math.Abs(A[row, col]) > Math.Abs(A[pivotRow, col]))
                        pivotRow = row;
                }

                // Swap rows
                for (int j = 0; j < n; j++)
                {
                    (A[col, j], A[pivotRow, j]) = (A[pivotRow, j], A[col, j]);
                    (I[col, j], I[pivotRow, j]) = (I[pivotRow, j], I[col, j]);
                }

                // Forward elimination
                float div = A[col, col];
                if (Math.Abs(div) < 1e-10f) continue;

                for (int j = 0; j < n; j++)
                {
                    A[col, j] /= div;
                    I[col, j] /= div;
                }

                for (int row = 0; row < n; row++)
                {
                    if (row == col) continue;
                    float factor = A[row, col];
                    for (int j = 0; j < n; j++)
                    {
                        A[row, j] -= factor * A[col, j];
                        I[row, j] -= factor * I[col, j];
                    }
                }
            }

            return I;
        }
    }
}