namespace UnityBrowser.Unity;

/// <summary>Row-major 4x4 matrices (column-vector convention: v' = M v), as used by Unity and COLLADA.</summary>
public static class Mat4
{
    public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    public static double[] Multiply(double[] a, double[] b)
    {
        var m = new double[16];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[r * 4 + k] * b[k * 4 + c];
                m[r * 4 + c] = s;
            }
        return m;
    }

    /// <summary>Converts a Unity (left-handed) matrix to right-handed by mirroring X: S * M * S.</summary>
    public static double[] MirrorX(double[] m)
    {
        var r = (double[])m.Clone();
        for (int i = 0; i < 4; i++)
        {
            r[0 * 4 + i] = -r[0 * 4 + i];
            r[i * 4 + 0] = -r[i * 4 + 0];
        }
        return r; // element [0,0] is negated twice, i.e. unchanged
    }

    /// <summary>T * R * S from a translation, a unit quaternion (x, y, z, w) and a scale.</summary>
    public static double[] Trs(double tx, double ty, double tz, double qx, double qy, double qz, double qw,
        double sx, double sy, double sz)
    {
        double xx = qx * qx, yy = qy * qy, zz = qz * qz, xy = qx * qy, xz = qx * qz, yz = qy * qz;
        double wx = qw * qx, wy = qw * qy, wz = qw * qz;
        return new[]
        {
            (1 - 2 * (yy + zz)) * sx, 2 * (xy - wz) * sy,       2 * (xz + wy) * sz,       tx,
            2 * (xy + wz) * sx,       (1 - 2 * (xx + zz)) * sy, 2 * (yz - wx) * sz,       ty,
            2 * (xz - wy) * sx,       2 * (yz + wx) * sy,       (1 - 2 * (xx + yy)) * sz, tz,
            0, 0, 0, 1,
        };
    }

    /// <summary>Normalises the three axis columns (a zero axis becomes the unit axis), keeping the translation.</summary>
    public static double[] RemoveScale(double[] m)
    {
        var r = (double[])m.Clone();
        for (int c = 0; c < 3; c++)
        {
            double len = Math.Sqrt(r[c] * r[c] + r[4 + c] * r[4 + c] + r[8 + c] * r[8 + c]);
            for (int row = 0; row < 3; row++)
                r[row * 4 + c] = len > 1e-9 ? r[row * 4 + c] / len : (row == c ? 1 : 0);
        }
        return r;
    }

    public static (double X, double Y, double Z) Transform(double[] m, double x, double y, double z) => (
        m[0] * x + m[1] * y + m[2] * z + m[3],
        m[4] * x + m[5] * y + m[6] * z + m[7],
        m[8] * x + m[9] * y + m[10] * z + m[11]);

    /// <summary>General 4x4 inverse (cofactor expansion).</summary>
    public static double[] Invert(double[] m)
    {
        var inv = new double[16];
        inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
        inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
        inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
        inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
        inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
        inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
        inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
        inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
        inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
        inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
        inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
        inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
        inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
        inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
        inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
        inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
        double det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
        if (Math.Abs(det) < 1e-20) return Identity();
        for (int i = 0; i < 16; i++) inv[i] /= det;
        return inv;
    }
}
